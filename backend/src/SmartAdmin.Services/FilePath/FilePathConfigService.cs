using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="IFilePathConfigService"/> 默认实现。保存前先规范化再校验,落库的永远是规范形式:
/// <list type="bullet">
///   <item>子文件夹:<c>\</c> 换成 <c>/</c>,空存 null,不能有点开头的段;</item>
///   <item>只读行:后缀白名单和大小上限不保存(下载不看后缀、不限大小),避免读回来一份没人用的配置;</item>
///   <item>可写行:后缀白名单和大小上限必填,后缀转小写去重;</item>
///   <item>同名处理只对「业务指定路径」有意义,其余命名规则一律存「拒绝」;</item>
///   <item>角色 Id:升序去重的逗号串,空集合存 null。</item>
/// </list>
/// 测试连接和在线状态经 <see cref="IFilePathCircuit"/> 探测:不受熔断阻挡、失败不打开熔断,成功时关闭熔断,仍受在途上限约束。
/// </summary>
public class FilePathConfigService(
    IRepository<SysFilePath> paths,
    IRepository<SysFileServer> servers,
    ICacheProvider cache,
    IFileServerConnector connector,
    AdminFilePathOptions options,
    IFilePathCircuit circuit) : IFilePathConfigService
{
    private const int MaxCodeLength = 64;

    /// <inheritdoc />
    public virtual async Task<PagedList<FilePathView>> PageAsync(FilePathPageInput input)
    {
        var paged = await paths.AsQueryable()
            .WhereIF(!string.IsNullOrEmpty(input.Keyword), p => p.Code.Contains(input.Keyword!) || p.Name.Contains(input.Keyword!))
            .WhereIF(input.ServerId.HasValue, p => p.ServerId == input.ServerId!.Value)
            .WhereIF(input.Enabled.HasValue, p => p.Enabled == input.Enabled!.Value)
            .ToPagedListAsync(input, q => q.OrderBy(p => p.Id));

        var serverIds = paged.Items.Select(p => p.ServerId).Distinct().ToList();
        var names = serverIds.Count == 0
            ? []
            : (await servers.AsQueryable().Where(s => serverIds.Contains(s.Id)).ToListAsync()).ToDictionary(s => s.Id, s => s.Name);

        return new PagedList<FilePathView>
        {
            Current = paged.Current,
            Size = paged.Size,
            Total = paged.Total,
            Items = paged.Items.Select(p => ToView(p, names.GetValueOrDefault(p.ServerId))).ToList(),
        };
    }

    /// <inheritdoc />
    public virtual async Task<FilePathView> GetAsync(long id)
    {
        var entity = await paths.GetRequiredAsync(id, ErrorCode.FilePathNotFound);
        return ToView(entity, (await servers.GetByIdAsync(entity.ServerId))?.Name);
    }

    /// <inheritdoc />
    public virtual async Task<long> AddAsync(FilePathAddInput input)
    {
        var normalized = Normalize(input);
        await ValidateAsync(normalized, excludingId: null);

        var entity = new SysFilePath { Code = normalized.Code };
        Apply(entity, normalized);
        await paths.InsertAsync(entity);

        await InvalidateAsync();
        return entity.Id;
    }

    /// <inheritdoc />
    public virtual async Task UpdateAsync(long id, FilePathUpdateInput input)
    {
        var entity = await paths.GetRequiredAsync(id, ErrorCode.FilePathNotFound);
        var normalized = Normalize(new FilePathAddInput(
            entity.Code, input.Name, input.ServerId, input.Folder, input.NamingRule, input.OnConflict, input.ReadOnly,
            input.AllowedExtensions, input.MaxSizeMb, input.UploadRoleIds, input.DownloadRoleIds, input.Enabled, input.Remark));
        await ValidateAsync(normalized, excludingId: id);

        Apply(entity, normalized);
        await paths.UpdateAsync(entity);

        await InvalidateAsync();
    }

    /// <inheritdoc />
    public virtual async Task DeleteAsync(long id)
    {
        await paths.GetRequiredAsync(id, ErrorCode.FilePathNotFound);
        await paths.DeleteAsync(id);
        await InvalidateAsync();
    }

    /// <inheritdoc />
    public virtual FilePathEnvironment GetEnvironment() => new(options.AllowedRoots, connector.SupportsCredentials);

    /// <inheritdoc />
    public virtual async Task<FileAccessTestResult> TestAsync(FilePathTestInput input, CancellationToken ct = default)
    {
        var folder = NormalizeFolder(input.Folder);
        var server = await servers.GetRequiredAsync(input.ServerId, ErrorCode.FileServerNotFound);
        AdminException.ThrowIf(!FilePathRules.IsAllowedRoot(server.RootPath, options.AllowedRoots), ErrorCode.FileServerRootNotAllowed);

        var target = new FileServerTarget(server.Id, server.RootPath, server.UserName, server.PasswordProtected);
        return await FileAccessProbe.TestAsync(
            t => circuit.RunAsync(target.Id, () => ProbeAsync(target, folder, write: !input.ReadOnly, retryFailed: true, t), probe: true), ct);
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<FileAccessStatus>> StatusAsync(IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return [];

        var rows = (await paths.AsQueryable().Where(p => idList.Contains(p.Id)).ToListAsync(ct)).ToDictionary(p => p.Id);
        var serverIds = rows.Values.Select(p => p.ServerId).Distinct().ToList();
        var serverRows = serverIds.Count == 0
            ? []
            : (await servers.AsQueryable().Where(s => serverIds.Contains(s.Id)).ToListAsync(ct)).ToDictionary(s => s.Id);

        return await Task.WhenAll(idList.Where(rows.ContainsKey).Select(id =>
        {
            var path = rows[id];
            var target = serverRows.TryGetValue(path.ServerId, out var s)
                ? new FileServerTarget(s.Id, s.RootPath, s.UserName, s.PasswordProtected)
                : null;
            return FileAccessProbe.StatusAsync(id, t => target is null
                ? throw new DirectoryNotFoundException()
                : circuit.RunAsync(target.Id, () => ProbeAsync(target, path.Folder, write: false, retryFailed: false, t), probe: true), ct);
        }));
    }

    // ── 内部辅助 ─────────────────────────────────────────────────────

    private static FilePathView ToView(SysFilePath p, string? serverName) =>
        new(p.Id, p.Code, p.Name, p.ServerId, serverName, p.Folder, p.NamingRule, p.OnConflict, p.ReadOnly,
            p.AllowedExtensions, p.MaxSizeMb, FilePathRules.ParseRoleIds(p.UploadRoleIds), FilePathRules.ParseRoleIds(p.DownloadRoleIds),
            p.Enabled, p.Remark);

    /// <summary>把规范化后的入参写到实体上(编码由新增时设置,不在这里改)。</summary>
    private static void Apply(SysFilePath entity, FilePathAddInput input)
    {
        entity.Name = input.Name;
        entity.ServerId = input.ServerId;
        entity.Folder = input.Folder;
        entity.NamingRule = input.NamingRule;
        entity.OnConflict = input.OnConflict;
        entity.ReadOnly = input.ReadOnly;
        entity.AllowedExtensions = input.AllowedExtensions;
        entity.MaxSizeMb = input.MaxSizeMb;
        entity.UploadRoleIds = FilePathRules.FormatRoleIds(input.UploadRoleIds);
        entity.DownloadRoleIds = FilePathRules.FormatRoleIds(input.DownloadRoleIds);
        entity.Enabled = input.Enabled;
        entity.Remark = input.Remark;
    }

    /// <summary>
    /// 规范化入参(纯函数,不碰库):子文件夹、后缀白名单的格式在这里校验;
    /// 只读行丢掉后缀和大小,可写行缺后缀或大小抛 <see cref="ErrorCode.FilePathRuleIncomplete"/>;非业务指定路径的同名处理归为拒绝。
    /// </summary>
    protected virtual FilePathAddInput Normalize(FilePathAddInput input)
    {
        var normalized = input with
        {
            Folder = NormalizeFolder(input.Folder),
            OnConflict = input.NamingRule == FilePathNamingRule.BusinessPath ? input.OnConflict : FilePathConflictMode.Reject,
        };
        if (input.ReadOnly) return normalized with { AllowedExtensions = null, MaxSizeMb = null };

        var extensions = FilePathRules.ParseExtensions(input.AllowedExtensions).Distinct().ToList();
        AdminException.ThrowIf(extensions.Count == 0 || input.MaxSizeMb is not > 0, ErrorCode.FilePathRuleIncomplete);
        return normalized with { AllowedExtensions = string.Join(',', extensions) };
    }

    /// <summary>子文件夹规范化(保存和测试连接共用):<c>\</c> 换成 <c>/</c>,空为 null,不能有点开头的段,也不能有 Windows 保留设备名的段。</summary>
    protected virtual string? NormalizeFolder(string? folder)
    {
        var normalized = FilePathRules.NormalizeRelative(folder?.Trim(), allowEmpty: true);
        // 下载一律把点开头的段当不存在,文件夹落在这样的目录里,存进去的文件就取不出来;
        // 设备名的段(CON、NUL.txt)在 Windows 上建不出目录,这一行保存了也永远用不了
        if (FilePathRules.HasHiddenSegment(normalized) || normalized.Split('/').Any(FilePathRules.IsReservedDeviceName))
            throw new AdminException(ErrorCode.FilePathInvalidPath);
        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>
    /// 探测一个文件夹:确保服务器已连上;可写时先建文件夹,再写入、读回、删除一个 <c>.smartadmin-probe-{GUID}</c> 文件;只读时只确认文件夹存在。
    /// 根路径不在白名单内(白名单可能在保存之后改小)按拒绝访问处理,不去碰这个路径。
    /// </summary>
    protected virtual async Task ProbeAsync(FileServerTarget server, string? folder, bool write, bool retryFailed, CancellationToken ct)
    {
        if (!FilePathRules.IsAllowedRoot(server.RootPath, options.AllowedRoots)) throw new UnauthorizedAccessException();

        await connector.EnsureConnectedAsync(server, retryFailed, ct);
        var fullPath = folder is null ? Path.GetFullPath(server.RootPath) : FilePathRules.Combine(server.RootPath, folder);
        if (!write)
        {
            FileAccessProbe.RequireFolder(fullPath);
            return;
        }

        Directory.CreateDirectory(fullPath);
        var probe = Path.Combine(fullPath, $".smartadmin-probe-{Guid.NewGuid():N}");
        var content = Guid.NewGuid().ToByteArray();
        try
        {
            await File.WriteAllBytesAsync(probe, content, ct);
            if (!(await File.ReadAllBytesAsync(probe, ct)).AsSpan().SequenceEqual(content))
                throw new IOException("探测文件读回的内容与写入的不一致");
        }
        finally
        {
            File.Delete(probe);
        }
    }

    /// <summary>
    /// 规范化之后查库的校验:新增时编码格式与唯一(编码保存后不能改,更新不再检查),文件服务器须存在。
    /// </summary>
    protected virtual async Task ValidateAsync(FilePathAddInput input, long? excludingId)
    {
        if (excludingId is null)
        {
            AdminException.ThrowIf(!IsValidCode(input.Code), ErrorCode.FilePathCodeInvalid);
            await EnsureCodeAvailableAsync(input.Code);
        }

        AdminException.ThrowIf(await servers.GetByIdAsync(input.ServerId) is null, ErrorCode.FileServerNotFound);
    }

    /// <summary>编码只允许字母、数字、<c>.</c>、<c>_</c>、<c>-</c>,1–64 个字符。逐字符判断而不用正则,免得 <c>$</c> 放过末尾换行。</summary>
    private static bool IsValidCode(string? code) =>
        code is { Length: >= 1 and <= MaxCodeLength } && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>编码唯一性校验,纳入软删行(唯一索引覆盖已软删行,漏检会撞库唯一约束抛原生 500)。</summary>
    protected virtual async Task EnsureCodeAvailableAsync(string code) =>
        AdminException.ThrowIf(await paths.AnyIncludingDeletedAsync(p => p.Code == code), ErrorCode.FilePathCodeExists);

    /// <summary>失效文件路径的整表读穿透缓存;新增/修改/删除成功后都要调。</summary>
    protected virtual Task InvalidateAsync() => cache.RemoveAsync(CacheKeys.FilePaths());
}
