using SqlSugar;
using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="IFileServerService"/> 默认实现。
/// <para>密码和 AI 厂商的 API Key 同一套规则:出参只有「是否已设置」,留空表示不改,数据保护密钥是进程内临时密钥时拒绝保存
/// (重启后信封就解不开了)。清空账号时密码一并清空。</para>
/// </summary>
public class FileServerService(
    IRepository<SysFileServer> servers,
    IRepository<SysFilePath> paths,
    IDataProtectionKeyProvider keyProvider,
    ISecretProtector secretProtector,
    ICacheProvider cache,
    IFileServerConnector connector,
    AdminFilePathOptions options) : IFileServerService
{
    /// <inheritdoc />
    public virtual async Task<PagedList<FileServerView>> PageAsync(FileServerPageInput input)
    {
        var paged = await servers.AsQueryable()
            .WhereIF(!string.IsNullOrEmpty(input.Keyword), s => s.Name.Contains(input.Keyword!) || s.RootPath.Contains(input.Keyword!))
            .WhereIF(input.Enabled.HasValue, s => s.Enabled == input.Enabled!.Value)
            .ToPagedListAsync(input, q => q.OrderBy(s => s.Id));

        return new PagedList<FileServerView>
        {
            Current = paged.Current,
            Size = paged.Size,
            Total = paged.Total,
            Items = paged.Items.Select(ToView).ToList(),
        };
    }

    /// <inheritdoc />
    public virtual async Task<FileServerView> GetAsync(long id) =>
        ToView(await servers.GetRequiredAsync(id, ErrorCode.FileServerNotFound));

    /// <inheritdoc />
    public virtual async Task<long> AddAsync(FileServerSaveInput input)
    {
        await ValidateAsync(input, excludingId: null);

        var entity = new SysFileServer();
        Apply(entity, input);
        await servers.InsertAsync(entity);

        await InvalidateAsync();
        return entity.Id;
    }

    /// <inheritdoc />
    public virtual async Task UpdateAsync(long id, FileServerSaveInput input)
    {
        var entity = await servers.GetRequiredAsync(id, ErrorCode.FileServerNotFound);
        await ValidateAsync(input, excludingId: id);

        Apply(entity, input);
        await servers.UpdateAsync(entity);

        await InvalidateAsync();
    }

    /// <inheritdoc />
    public virtual async Task DeleteAsync(long id)
    {
        await servers.GetRequiredAsync(id, ErrorCode.FileServerNotFound);
        AdminException.ThrowIf(await paths.AnyAsync(p => p.ServerId == id), ErrorCode.FileServerInUse);

        await servers.DeleteAsync(id);
        await InvalidateAsync();
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<FileServerOption>> OptionsAsync() =>
        (await servers.AsQueryable().OrderBy(s => s.Name).ToListAsync())
        .Select(s => new FileServerOption(s.Id, s.Name, s.RootPath, s.Enabled)).ToList();

    // ── 内部辅助 ─────────────────────────────────────────────────────

    private static FileServerView ToView(SysFileServer s) =>
        new(s.Id, s.Name, s.RootPath, s.UserName, !string.IsNullOrEmpty(s.PasswordProtected), s.Enabled, s.Remark);

    /// <summary>账号去掉首尾空白,全空白按没填(null)处理——存库、查冲突、判断「填了账号」都用这一个口径。</summary>
    private static string? CleanUserName(string? userName) =>
        string.IsNullOrWhiteSpace(userName) ? null : userName.Trim();

    private static string CleanRootPath(string? rootPath) => rootPath?.Trim() ?? "";

    /// <summary>把入参写到实体上(校验已通过)。</summary>
    private void Apply(SysFileServer entity, FileServerSaveInput input)
    {
        entity.Name = input.Name;
        entity.RootPath = CleanRootPath(input.RootPath);
        entity.Enabled = input.Enabled;
        entity.Remark = input.Remark;
        ApplyCredential(entity, input.UserName, input.Password);
    }

    /// <summary>
    /// 保存前的整体校验,顺序即检查顺序:根路径非空 → 填了账号时须是共享路径(仅连接器支持账号时)→
    /// 同主机账号一致 → 在白名单内 → 名称唯一。
    /// </summary>
    protected virtual async Task ValidateAsync(FileServerSaveInput input, long? excludingId)
    {
        var rootPath = CleanRootPath(input.RootPath);
        var userName = CleanUserName(input.UserName);
        AdminException.ThrowIf(rootPath.Length == 0, ErrorCode.FileServerRootNotAllowed);

        AdminException.ThrowIf(
            connector.SupportsCredentials && userName is not null && !FilePathRules.TryGetShare(rootPath, out _, out _),
            ErrorCode.FileServerShareRequired);

        await EnsureHostAccountConsistentAsync(rootPath, userName, excludingId);

        // 相对路径会按进程当前目录解析,结果随启动方式漂移,所以只收绝对路径
        AdminException.ThrowIf(
            !Path.IsPathFullyQualified(rootPath) || !FilePathRules.IsUnderAnyRoot(rootPath, options.AllowedRoots),
            ErrorCode.FileServerRootNotAllowed);

        await EnsureNameAvailableAsync(input.Name, excludingId);
    }

    /// <summary>
    /// 同一台主机只能用同一个账号:Windows 的 SMB 客户端在一个登录会话里对同一台服务器只接受一套凭据,第二套会返回 1219。
    /// 只比较能解析出主机的共享路径;主机名按写法比较(不区分大小写,<c>\\nas01</c> 和 <c>\\10.0.0.5</c> 算两台),
    /// 账号也不区分大小写,null 和空串是同一个值。已停用的行也参与比较,免得启用时才冲突。
    /// </summary>
    protected virtual async Task EnsureHostAccountConsistentAsync(string rootPath, string? userName, long? excludingId)
    {
        if (!FilePathRules.TryGetShare(rootPath, out var host, out _)) return;

        // 文件服务器是运维手工登记的,总共几行,取回内存里比较
        var others = await servers.AsQueryable()
            .WhereIF(excludingId.HasValue, s => s.Id != excludingId!.Value)
            .ToListAsync();
        var conflict = others.Any(s =>
            FilePathRules.TryGetShare(s.RootPath, out var otherHost, out _)
            && string.Equals(otherHost, host, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(CleanUserName(s.UserName) ?? "", userName ?? "", StringComparison.OrdinalIgnoreCase));
        AdminException.ThrowIf(conflict, ErrorCode.FileServerAccountConflict, new Dictionary<string, object?> { ["host"] = host });
    }

    /// <summary>名称唯一性校验,纳入软删行(唯一索引覆盖已软删行,漏检会撞库唯一约束抛原生 500)。</summary>
    protected virtual async Task EnsureNameAvailableAsync(string name, long? excludingId)
    {
        var exists = await servers.AsQueryable().ClearFilter<ISoftDelete>()
            .WhereIF(excludingId.HasValue, s => s.Id != excludingId!.Value)
            .AnyAsync(s => s.Name == name);
        AdminException.ThrowIf(exists, ErrorCode.FileServerNameExists);
    }

    /// <summary>
    /// 账号和密码的写入规则:账号为空 → 账号、密码都清空;密码为 null/空串 → 不改;密码非空 → 先查数据保护密钥是否为
    /// 进程内临时密钥(是则拒绝保存,抛 <see cref="ErrorCode.FileServerKeyMissing"/>),再加密写入。明文只在本次调用经过内存,不写日志。
    /// </summary>
    protected virtual void ApplyCredential(SysFileServer entity, string? userName, string? password)
    {
        entity.UserName = CleanUserName(userName);
        if (entity.UserName is null)
        {
            entity.PasswordProtected = null;
            return;
        }

        if (string.IsNullOrEmpty(password)) return;
        AdminException.ThrowIf(keyProvider.IsEphemeral, ErrorCode.FileServerKeyMissing);
        entity.PasswordProtected = secretProtector.Protect(password);
    }

    /// <summary>失效文件服务器的整表读穿透缓存;新增/修改/删除成功后都要调。</summary>
    protected virtual Task InvalidateAsync() => cache.RemoveAsync(CacheKeys.FileServers());
}
