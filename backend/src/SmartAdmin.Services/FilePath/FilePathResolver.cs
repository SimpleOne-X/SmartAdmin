using Microsoft.Extensions.Logging;
using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="IFilePathResolver"/> 默认实现。两张表各自整表读穿透缓存、变更即失效(由各自的管理服务负责),
/// 规则在每次解析时由这两份缓存组合——改服务器或改文件路径,下一次解析就是新的。
/// </summary>
public class FilePathResolver(
    IRepository<SysFilePath> paths,
    IRepository<SysFileServer> servers,
    ICacheProvider cache,
    AdminCacheOptions cacheOptions,
    IFileServerConnector connector,
    AdminFilePathOptions options,
    ILogger<FilePathResolver> logger,
    IFilePathCircuit circuit) : IFilePathResolver
{
    /// <inheritdoc />
    public virtual async Task<FilePathRule> GetRuleAsync(string code, CancellationToken ct = default)
    {
        var path = (await LoadPathsAsync(ct)).FirstOrDefault(p => p.Enabled && p.Code == code);
        AdminException.ThrowIf(path is null, ErrorCode.FilePathNotFound);

        var server = (await LoadServersAsync(ct)).FirstOrDefault(s => s.Enabled && s.Id == path.ServerId);
        AdminException.ThrowIf(server is null, ErrorCode.FileServerNotFound);

        return ToRule(path, server);
    }

    /// <inheritdoc />
    public virtual async Task<FilePathTarget> ResolveAsync(string code, CancellationToken ct = default)
    {
        var rule = await GetRuleAsync(code, ct);

        // 白名单可能在保存之后改小、暂存目录可能挪动,所以读取时再判一次;不能依赖保存时的结果
        AdminException.ThrowIf(!FilePathRules.IsAllowedRoot(rule.Server.RootPath, options), FilePathRules.RootRejection(options));

        // 服务器刚出过网络类失败就直接报,不再去连:连一次要卡到 SMB 超时
        circuit.ThrowIfOpen(rule.Server.Id);
        try
        {
            await connector.EnsureConnectedAsync(rule.Server, retryFailed: false, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException)
        {
            circuit.ReportFailure(rule.Server.Id, e);
            throw FileAccessFailure.ToError(e);
        }

        return new FilePathTarget(rule, rule.Folder is null
            ? Path.GetFullPath(rule.Server.RootPath)
            : FilePathRules.Combine(rule.Server.RootPath, rule.Folder));
    }

    /// <inheritdoc />
    public virtual string Combine(FilePathTarget target, string relativePath) =>
        FilePathRules.Combine(target.FolderFullPath, FilePathRules.NormalizeRelative(relativePath, allowEmpty: false));

    /// <summary>配置行 + 文件服务器 → 规则。后缀、角色在这里从库里的逗号串拆成列表。</summary>
    protected virtual FilePathRule ToRule(SysFilePath path, SysFileServer server) =>
        new(path.Id, path.Code, new FileServerTarget(server.Id, server.RootPath, server.UserName, server.PasswordProtected),
            path.Folder, path.NamingRule, path.OnConflict, path.ReadOnly,
            FilePathRules.ParseExtensions(path.AllowedExtensions), path.MaxSizeMb,
            ParseRoleIds(path, nameof(SysFilePath.UploadRoleIds), path.UploadRoleIds),
            ParseRoleIds(path, nameof(SysFilePath.DownloadRoleIds), path.DownloadRoleIds));

    /// <summary>
    /// 严格解析角色串:有解析不出的项就拒绝这一行。空 = 所有登录用户,所以坏掉的串若按宽松方式丢弃坏项,
    /// 会悄悄变成不限角色(种子数据、手工改库都可能写出这种值)。日志只记编码和列名,不带行里其它数据。
    /// </summary>
    private IReadOnlyList<long> ParseRoleIds(SysFilePath path, string column, string? csv)
    {
        if (FilePathRules.TryParseRoleIds(csv, out var roleIds)) return roleIds;

        logger.LogWarning("文件路径 {Code} 的 {Column} 列不是合法的角色 Id 列表,该行已拒绝使用", path.Code, column);
        throw new AdminException(ErrorCode.FilePathRuleIncomplete);
    }

    /// <summary>文件路径整表读穿透缓存;变更即失效(由 <see cref="FilePathConfigService"/> 负责失效)。</summary>
    protected virtual async Task<IReadOnlyList<SysFilePath>> LoadPathsAsync(CancellationToken ct)
    {
        var cached = await cache.GetAsync<List<SysFilePath>>(CacheKeys.FilePaths(), ct);
        if (cached is not null) return cached;

        var rows = await paths.AsQueryable().ToListAsync(ct);
        await cache.SetAsync(CacheKeys.FilePaths(), rows, CacheTtl(), ct);
        return rows;
    }

    /// <summary>文件服务器整表读穿透缓存;变更即失效(由 <see cref="FileServerService"/> 负责失效)。</summary>
    protected virtual async Task<IReadOnlyList<SysFileServer>> LoadServersAsync(CancellationToken ct)
    {
        var cached = await cache.GetAsync<List<SysFileServer>>(CacheKeys.FileServers(), ct);
        if (cached is not null) return cached;

        var rows = await servers.AsQueryable().ToListAsync(ct);
        await cache.SetAsync(CacheKeys.FileServers(), rows, CacheTtl(), ct);
        return rows;
    }

    private TimeSpan? CacheTtl() =>
        cacheOptions.PermissionMinutes > 0 ? TimeSpan.FromMinutes(cacheOptions.PermissionMinutes) : null;
}
