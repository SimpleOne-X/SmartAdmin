using Microsoft.Extensions.Logging;
using SqlSugar;
using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="IFileServerService"/> 默认实现。
/// <para>密码和 AI 厂商的 API Key 同一套规则:出参只有「是否已设置」,留空表示不改,数据保护密钥是进程内临时密钥时拒绝保存
/// (重启后信封就解不开了)。清空账号时密码一并清空;修改时换了主机或账号而密码留空,拒绝保存(旧密码不能沿用)。</para>
/// <para>Windows 对一台主机只接受一套凭据,所以同一主机的各行账号和密码必须一致:新增或把根路径换到另一台主机时,
/// 账号与那台主机已有的行不一致就拒绝;在同一台主机上修改账号或密码时,同主机的其他行在同一个事务里一起改成这一行的账号和密码。</para>
/// <para>测试连接和在线状态经 <see cref="IFilePathCircuit"/> 探测:不受熔断阻挡、失败不打开熔断,连得上(含拒绝访问这类失败)时关闭熔断,仍受在途上限约束。</para>
/// </summary>
public class FileServerService(
    IRepository<SysFileServer> servers,
    IRepository<SysFilePath> paths,
    IDataProtectionKeyProvider keyProvider,
    ISecretProtector secretProtector,
    ICacheProvider cache,
    IFileServerConnector connector,
    AdminFilePathOptions options,
    ILogger<FileServerService> logger,
    IFilePathCircuit circuit) : IFileServerService
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
        await ValidateAsync(input, saved: null);

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
        await ValidateAsync(input, entity);

        // 级联只在修改且主机没变时发生;改到另一台主机的,ValidateAsync 已经要求账号与那台主机一致
        var host = ShareHost(entity.RootPath);
        var sameHost = host is not null && string.Equals(host, ShareHost(CleanRootPath(input.RootPath)), StringComparison.OrdinalIgnoreCase);
        var savedUserName = entity.UserName;
        Apply(entity, input);
        var passwordChanged = entity.UserName is not null && !string.IsNullOrEmpty(input.Password);
        // 这一行的账号(按原样)和密码都没改就不级联:只改名称不能把它的信封复制给别的行
        var credentialChanged = passwordChanged || !string.Equals(savedUserName, entity.UserName, StringComparison.Ordinal);
        await servers.ExecuteInTransactionAsync(async () =>
        {
            // 账号按原样比较:只差大小写也要统一写法;信封每次加密都不同,只有这次改了密码才拿它比
            var others = !sameHost || !credentialChanged ? [] : (await servers.AsQueryable().Where(s => s.Id != entity.Id).ToListAsync())
                .Where(s => string.Equals(ShareHost(s.RootPath), host, StringComparison.OrdinalIgnoreCase)
                    && (!string.Equals(s.UserName, entity.UserName, StringComparison.Ordinal)
                        || passwordChanged && s.PasswordProtected != entity.PasswordProtected))
                .ToList();
            await CascadeHostCredentialAsync(entity, others);
        });

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

    /// <inheritdoc />
    public virtual async Task<FileAccessTestResult> TestAsync(FileServerTestInput input, CancellationToken ct = default)
    {
        var target = await ToTestTargetAsync(input);
        return await FileAccessProbe.TestAsync(
            t => circuit.RunAsync(target.Id, () => ProbeAsync(target, retryFailed: true, readEntries: true, t), probe: true), ct);
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<FileAccessStatus>> StatusAsync(IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return [];

        var rows = (await servers.AsQueryable().Where(s => idList.Contains(s.Id)).ToListAsync(ct)).ToDictionary(s => s.Id);
        return await Task.WhenAll(idList.Where(rows.ContainsKey).Select(id =>
        {
            var s = rows[id];
            var target = new FileServerTarget(s.Id, s.RootPath, s.UserName, s.PasswordProtected);
            return FileAccessProbe.StatusAsync(id,
                t => circuit.RunAsync(id, () => ProbeAsync(target, retryFailed: false, readEntries: false, t), probe: true), ct);
        }));
    }

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
    /// 保存前的整体校验:先 <see cref="ValidateLocationAsync"/>,修改时再查密码是否必须重填,最后查名称唯一。
    /// <paramref name="saved"/> 是修改前的行,新增时为 null。修改且主机没变时不查同主机账号一致:同主机的其他行会随这一行一起改。
    /// </summary>
    protected virtual async Task ValidateAsync(FileServerSaveInput input, SysFileServer? saved)
    {
        var rootPath = CleanRootPath(input.RootPath);
        var userName = CleanUserName(input.UserName);
        var checkHostAccount = saved is null || ShareHost(saved.RootPath) is not { } host
            || !string.Equals(host, ShareHost(rootPath), StringComparison.OrdinalIgnoreCase);
        await ValidateLocationAsync(rootPath, userName, saved?.Id, checkHostAccount);

        // 旧密码只在主机和账号都没变时沿用;否则密码留空就等于把空信封存下来,级联时还会复制给整台主机。
        // 连接器不用账号的平台上配置页填不了密码,账号和密码只是原样留着
        AdminException.ThrowIf(
            connector.SupportsCredentials && saved is not null && userName is not null && string.IsNullOrEmpty(input.Password)
            && !CanReuseSavedPassword(saved, rootPath, userName),
            ErrorCode.FileServerPasswordRequired);

        await EnsureNameAvailableAsync(input.Name, saved?.Id);
    }

    /// <summary>
    /// 根路径与账号的校验,保存和测试连接共用(测试连接的入参没有名称,所以名称唯一不在这里)。
    /// 顺序即检查顺序:根路径非空且不含控制字符 → 填了账号时须是共享路径(仅连接器支持账号时)→ 同主机账号一致(<paramref name="checkHostAccount"/> 为真时)→ 在白名单内。
    /// </summary>
    protected virtual async Task ValidateLocationAsync(string rootPath, string? userName, long? excludingId, bool checkHostAccount)
    {
        // NUL 会让后面的路径函数直接抛 ArgumentException(变成 500),其余控制字符会原样带进连接和文件操作
        AdminException.ThrowIf(rootPath.Length == 0 || rootPath.Any(char.IsControl), ErrorCode.FileServerRootNotAllowed);

        AdminException.ThrowIf(
            connector.SupportsCredentials && userName is not null && !FilePathRules.TryGetShare(rootPath, out _, out _),
            ErrorCode.FileServerShareRequired);

        if (checkHostAccount) await EnsureHostAccountConsistentAsync(rootPath, userName, excludingId);

        AdminException.ThrowIf(!FilePathRules.IsAllowedRoot(rootPath, options.AllowedRoots), ErrorCode.FileServerRootNotAllowed);
    }

    /// <summary>
    /// 测试连接的目标:先走和保存相同的 <see cref="ValidateLocationAsync"/>(不通过抛 <see cref="AdminException"/>),
    /// 但不查同主机账号一致,管理员要能在保存前先试新账号。代价是:连接器对一台主机只保留一套凭据,
    /// 用新账号测试会断开本进程连着这台主机的旧连接,之后用旧账号的请求再按旧账号重连。
    /// 填了密码就现场加密成信封交给连接器,只在内存里,不落库;留空且是编辑已有的行时,
    /// 只有 <see cref="CanReuseSavedPassword"/> 放行才用库里已保存的信封,否则按没有密码去连。
    /// </summary>
    protected virtual async Task<FileServerTarget> ToTestTargetAsync(FileServerTestInput input)
    {
        var saved = input.Id is { } id ? await servers.GetRequiredAsync(id, ErrorCode.FileServerNotFound) : null;
        var rootPath = CleanRootPath(input.RootPath);
        var userName = CleanUserName(input.UserName);
        await ValidateLocationAsync(rootPath, userName, input.Id, checkHostAccount: false);

        var envelope = userName is null ? null
            : !string.IsNullOrEmpty(input.Password) ? secretProtector.Protect(input.Password)
            : saved is not null && CanReuseSavedPassword(saved, rootPath, userName) ? saved.PasswordProtected
            : null;
        return new FileServerTarget(input.Id ?? 0, rootPath, userName, envelope);
    }

    /// <summary>
    /// 密码留空时能否沿用这一行已保存的密码(测试连接与修改共用):主机和账号都没变才行,否则旧密码会被带去另一台主机,
    /// 或配给另一个账号去试。主机取共享路径里的主机名,不是共享路径的根按「本机」算;主机和账号都不区分大小写,账号 null 与空串相同。
    /// </summary>
    protected virtual bool CanReuseSavedPassword(SysFileServer saved, string rootPath, string? userName)
    {
        static string HostOf(string root) => FilePathRules.TryGetShare(root, out var host, out _) ? host : "";

        return string.Equals(HostOf(saved.RootPath), HostOf(rootPath), StringComparison.OrdinalIgnoreCase)
            && string.Equals(CleanUserName(saved.UserName) ?? "", userName ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 探测一台服务器:确保已连上,再确认根路径存在;<paramref name="readEntries"/> 时还要取到第一个条目(可读),不列完整个目录。
    /// 根路径不在白名单内(白名单可能在保存之后改小)按拒绝访问处理,不去碰这个路径。
    /// </summary>
    protected virtual async Task ProbeAsync(FileServerTarget target, bool retryFailed, bool readEntries, CancellationToken ct)
    {
        if (!FilePathRules.IsAllowedRoot(target.RootPath, options.AllowedRoots)) throw new UnauthorizedAccessException();

        await connector.EnsureConnectedAsync(target, retryFailed, ct);
        FileAccessProbe.RequireFolder(target.RootPath);
        if (readEntries) _ = Directory.EnumerateFileSystemEntries(target.RootPath).Any();
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

    /// <summary>
    /// 在事务里写入修改后的这一行,并把同主机的 <paramref name="others"/> 改成与它相同的账号和密码信封
    /// (改账号、只改密码、账号只差大小写、清空账号都算;只改名称等别的列时 <paramref name="others"/> 为空)。
    /// 本行和级联行合在一起按 Id 升序逐行整行更新:多个修改并发时加锁顺序一致,SQL Server 上不会互相死锁;整行更新也让审计字段照常刷新。
    /// </summary>
    protected virtual async Task CascadeHostCredentialAsync(SysFileServer saved, IReadOnlyList<SysFileServer> others)
    {
        foreach (var other in others)
        {
            other.UserName = saved.UserName;
            other.PasswordProtected = saved.PasswordProtected;
        }

        foreach (var row in others.Append(saved).OrderBy(s => s.Id))
            await servers.UpdateAsync(row);

        if (others.Count > 0)
            logger.LogInformation("文件服务器 {Id} 改了账号或密码,同一主机上的文件服务器 {CascadedIds} 一并改成相同的账号和密码",
                saved.Id, string.Join(",", others.Select(s => s.Id).Order()));
    }

    /// <summary>共享路径里的主机名(按写法,<c>\\nas01</c> 和 <c>\\10.0.0.5</c> 算两台);不是共享路径的根不属于任何主机,返回 null。</summary>
    private static string? ShareHost(string rootPath) => FilePathRules.TryGetShare(rootPath, out var host, out _) ? host : null;

    /// <summary>失效文件服务器的整表读穿透缓存;新增/修改/删除成功后都要调。</summary>
    protected virtual Task InvalidateAsync() => cache.RemoveAsync(CacheKeys.FileServers());
}
