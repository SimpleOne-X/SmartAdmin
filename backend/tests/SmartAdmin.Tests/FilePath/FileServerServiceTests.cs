using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>文件服务器管理:密码处理、根路径白名单、共享路径与同主机账号规则、名称唯一、被引用时禁止删除。</summary>
public class FileServerServiceTests
{
    private sealed class EphemeralKeyProvider : IDataProtectionKeyProvider
    {
        public bool IsEphemeral => true;
        public DataProtectionKeyMaterial GetCurrentKey() => throw new NotSupportedException("本测试不应真的走到加密这一步");
        public DataProtectionKeyMaterial GetKey(int version) => throw new NotSupportedException();
    }

    private static FileServerSaveInput Input(string name, string root, string? userName = null, string? password = null) =>
        new(name, root, userName, password, true, null);

    private static string NewName() => $"srv-{Guid.NewGuid():N}";

    private static async Task<SysFileServer> RowAsync(FilePathTestEnv env, long id) =>
        (await env.Service<IRepository<SysFileServer>>().GetByIdAsync(id))!;

    private static async Task<AdminException> Rejected(Task task) => await Assert.ThrowsAsync<AdminException>(() => task);

    [Fact]
    public async Task Password_is_encrypted_and_never_returned()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFileServerService>();

        var id = await service.AddAsync(Input(NewName(), env.Root, "svc-files", "p@ss"));

        var view = await service.GetAsync(id);
        Assert.True(view.HasPassword);
        // 出参上与密码沾边的只有「是否已设置」这个布尔标记,没有任何能带出密码或信封的属性
        var passwordProperties = typeof(FileServerView).GetProperties()
            .Where(p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)).ToList();
        var flag = Assert.Single(passwordProperties);
        Assert.Equal((nameof(FileServerView.HasPassword), typeof(bool)), (flag.Name, flag.PropertyType));

        var row = await RowAsync(env, id);
        Assert.NotNull(row.PasswordProtected);
        Assert.NotEqual("p@ss", row.PasswordProtected);
        Assert.Equal("p@ss", env.Service<ISecretProtector>().Unprotect(row.PasswordProtected!));
    }

    [Fact]
    public async Task Blank_password_on_update_keeps_existing()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFileServerService>();
        var name = NewName();
        var id = await service.AddAsync(Input(name, env.Root, "svc-files", "p@ss"));
        var before = (await RowAsync(env, id)).PasswordProtected;

        // 重新加密的信封带随机数,不会和原来相同;信封没变,说明根本没碰密码
        await service.UpdateAsync(id, Input(name, env.Root, "svc-files", null));
        Assert.Equal(before, (await RowAsync(env, id)).PasswordProtected);

        await service.UpdateAsync(id, Input(name, env.Root, "svc-files", ""));
        Assert.Equal(before, (await RowAsync(env, id)).PasswordProtected);
        Assert.True((await service.GetAsync(id)).HasPassword);
    }

    [Fact]
    public async Task Blank_password_on_update_kept_when_account_differs_only_in_case()
    {
        await using var env = FilePathTestEnv.Create(supportsCredentials: true, extraRoots: ["//nas01/files"]);
        var service = env.Service<IFileServerService>();
        var name = NewName();
        var id = await service.AddAsync(Input(name, "//nas01/files", "svc-files", "p@ss"));
        var before = (await RowAsync(env, id)).PasswordProtected;

        // 账号只差大小写算同一个账号:密码留空照样沿用,账号按新的写法保存
        await service.UpdateAsync(id, Input(name, "//nas01/files", "SVC-FILES", null));

        var row = await RowAsync(env, id);
        Assert.Equal(("SVC-FILES", before), (row.UserName, row.PasswordProtected));
    }

    [Theory]
    [InlineData("//nas02/files", "svc-files")]   // 换了主机
    [InlineData("//nas01/files", "svc-other")]   // 换了账号
    public async Task Blank_password_rejected_when_host_or_account_changes(string root, string userName)
    {
        await using var env = FilePathTestEnv.Create(supportsCredentials: true, extraRoots: ["//nas01/files", "//nas02/files"]);
        var service = env.Service<IFileServerService>();
        var name = NewName();
        var id = await service.AddAsync(Input(name, "//nas01/files", "svc-files", "p@ss"));
        var before = await RowAsync(env, id);

        // 旧密码不能带去新主机、也不能配给新账号;清空的信封更不能级联给整台主机,所以必须重填
        var ex = await Rejected(service.UpdateAsync(id, Input(name, root, userName, null)));

        Assert.Equal(ErrorCode.FileServerPasswordRequired, ex.Code);
        var after = await RowAsync(env, id);
        Assert.Equal((before.RootPath, before.UserName, before.PasswordProtected), (after.RootPath, after.UserName, after.PasswordProtected));
    }

    [Fact]
    public async Task Clearing_user_name_clears_password()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFileServerService>();
        var name = NewName();
        var id = await service.AddAsync(Input(name, env.Root, "svc-files", "p@ss"));

        await service.UpdateAsync(id, Input(name, env.Root, null, null));

        Assert.Null((await RowAsync(env, id)).PasswordProtected);
        var view = await service.GetAsync(id);
        Assert.False(view.HasPassword);
        Assert.Null(view.UserName);
    }

    [Fact]
    public async Task Ephemeral_key_rejects_saving_password()
    {
        await using var env = FilePathTestEnv.Create(
            overrides: s => s.Replace(ServiceDescriptor.Singleton<IDataProtectionKeyProvider>(new EphemeralKeyProvider())));
        var service = env.Service<IFileServerService>();

        var ex = await Rejected(service.AddAsync(Input(NewName(), env.Root, "svc-files", "p@ss")));
        Assert.Equal(ErrorCode.FileServerKeyMissing, ex.Code);

        var name = NewName();
        var id = await service.AddAsync(Input(name, env.Root, "svc-files", null));
        Assert.False((await service.GetAsync(id)).HasPassword);

        var update = await Rejected(service.UpdateAsync(id, Input(name, env.Root, "svc-files", "p@ss")));
        Assert.Equal(ErrorCode.FileServerKeyMissing, update.Code);
    }

    [Fact]
    public async Task Root_outside_allowed_roots_is_rejected()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFileServerService>();

        var outside = new[]
        {
            Path.Combine(Path.GetTempPath(), $"smart-fp-other-{Guid.NewGuid():N}"),
            env.Root + "2",                                   // 前缀相同但不是子目录
            Path.Combine(env.Root, "..", "smart-fp-escape"),  // 借 .. 逃出白名单
            "relative/dir",                                   // 不是绝对路径
            "",
        };
        foreach (var root in outside)
        {
            var ex = await Rejected(service.AddAsync(Input(NewName(), root)));
            Assert.True(ex.Code == ErrorCode.FileServerRootNotAllowed, $"根路径 '{root}' 应被拒绝,实际 {ex.Code}");
        }

        // 白名单根目录自身和它下面的子目录都放行
        await service.AddAsync(Input(NewName(), env.Root));
        await service.AddAsync(Input(NewName(), Path.Combine(env.Root, "sub")));
    }

    [Fact]
    public async Task Root_with_control_characters_is_rejected()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFileServerService>();

        // NUL 会让路径 API 直接抛 ArgumentException,其余控制字符会原样进到连接和文件操作里;都按根路径不可用拒绝
        foreach (var root in new[] { env.Root + "\0", Path.Combine(env.Root, "a\tb"), Path.Combine(env.Root, "x\u0001y") })
        {
            var add = await Rejected(service.AddAsync(Input(NewName(), root)));
            Assert.Equal(ErrorCode.FileServerRootNotAllowed, add.Code);

            var test = await Rejected(service.TestAsync(new FileServerTestInput(null, root, null, null)));
            Assert.Equal(ErrorCode.FileServerRootNotAllowed, test.Code);
        }
    }

    [Fact]
    public async Task User_name_requires_share_root_when_connector_supports_credentials()
    {
        await using (var strict = FilePathTestEnv.Create(supportsCredentials: true))
        {
            var ex = await Rejected(strict.Service<IFileServerService>().AddAsync(Input(NewName(), strict.Root, "svc-files")));
            Assert.Equal(ErrorCode.FileServerShareRequired, ex.Code);
        }

        await using var lenient = FilePathTestEnv.Create(supportsCredentials: false);
        var service = lenient.Service<IFileServerService>();
        var id = await service.AddAsync(Input(NewName(), lenient.Root, "svc-files"));
        Assert.Equal("svc-files", (await service.GetAsync(id)).UserName);
    }

    [Fact]
    public async Task Same_host_different_account_is_rejected()
    {
        await using var env = FilePathTestEnv.Create();
        await env.Service<IRepository<SysFileServer>>().InsertAsync(new SysFileServer
        {
            Name = NewName(), RootPath = @"\\nas01\files", UserName = "svc-a", Enabled = true,
        });
        var service = env.Service<IFileServerService>();

        // 主机名不区分大小写;args 里带的是本次输入的写法
        var conflict = await Rejected(service.AddAsync(Input(NewName(), @"\\NAS01\other", "svc-b")));
        Assert.Equal(ErrorCode.FileServerAccountConflict, conflict.Code);
        Assert.Equal("NAS01", conflict.Args!["host"]);

        // 账号只差大小写算同一个账号:过了账号这一关,停在白名单(测试环境里没有这台主机的根)
        var sameAccount = await Rejected(service.AddAsync(Input(NewName(), @"\\NAS01\other", "SVC-A")));
        Assert.Equal(ErrorCode.FileServerRootNotAllowed, sameAccount.Code);

        // 主机名按写法比较,IP 和主机名算两台
        var otherHost = await Rejected(service.AddAsync(Input(NewName(), @"\\10.0.0.5\x", "svc-b")));
        Assert.Equal(ErrorCode.FileServerRootNotAllowed, otherHost.Code);
    }

    [Fact]
    public async Task Update_does_not_conflict_with_its_own_row()
    {
        await using var env = FilePathTestEnv.Create();
        var row = new SysFileServer { Name = NewName(), RootPath = @"\\nas01\files", UserName = "svc-a", Enabled = true };
        await env.Service<IRepository<SysFileServer>>().InsertAsync(row);

        // 这一行是该主机上唯一的一行,改它的账号不算冲突;停在白名单
        var ex = await Rejected(env.Service<IFileServerService>().UpdateAsync(row.Id, Input(row.Name, @"\\nas01\files", "svc-b")));
        Assert.Equal(ErrorCode.FileServerRootNotAllowed, ex.Code);
    }

    [Fact]
    public async Task Name_unique_and_released_after_delete()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFileServerService>();
        var name = NewName();
        var id = await service.AddAsync(Input(name, env.Root));

        var ex = await Rejected(service.AddAsync(Input(name, env.Root)));
        Assert.Equal(ErrorCode.FileServerNameExists, ex.Code);

        await service.DeleteAsync(id);
        Assert.NotEqual(id, await service.AddAsync(Input(name, env.Root)));
    }

    [Fact]
    public async Task Renaming_to_an_existing_name_is_rejected()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFileServerService>();
        var (taken, mine) = (NewName(), NewName());
        await service.AddAsync(Input(taken, env.Root));
        var id = await service.AddAsync(Input(mine, env.Root));

        var ex = await Rejected(service.UpdateAsync(id, Input(taken, env.Root)));
        Assert.Equal(ErrorCode.FileServerNameExists, ex.Code);
        Assert.Equal(mine, (await service.GetAsync(id)).Name);

        // 保留自己原来的名字不算撞名
        await service.UpdateAsync(id, Input(mine, Path.Combine(env.Root, "sub")));
        Assert.Equal(Path.Combine(env.Root, "sub"), (await service.GetAsync(id)).RootPath);
    }

    [Fact]
    public async Task Same_host_same_account_is_accepted()
    {
        await using var env = FilePathTestEnv.Create(supportsCredentials: true, extraRoots: ["//nas01/files", "//nas01/scan"]);
        var service = env.Service<IFileServerService>();
        var files = await service.AddAsync(Input(NewName(), "//nas01/files", "svc-files", "p@ss"));

        // 同一台主机、同一个账号(只差大小写)的第二个共享照常保存,不算冲突
        var scan = await service.AddAsync(Input(NewName(), "//nas01/scan", "SVC-FILES", "p@ss"));

        var row = await RowAsync(env, scan);
        Assert.Equal(("//nas01/scan", "SVC-FILES"), (row.RootPath, row.UserName));
        Assert.Equal("p@ss", env.Service<ISecretProtector>().Unprotect(row.PasswordProtected!));
        Assert.Equal("svc-files", (await RowAsync(env, files)).UserName);
    }

    [Fact]
    public async Task Server_in_use_cannot_be_deleted()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFileServerService>();
        var id = await env.AddServerAsync();
        var paths = env.Service<IRepository<SysFilePath>>();
        var path = new SysFilePath { Code = $"p-{Guid.NewGuid():N}", Name = "订单附件", ServerId = id, Folder = "Order", Enabled = true };
        await paths.InsertAsync(path);

        var ex = await Rejected(service.DeleteAsync(id));
        Assert.Equal(ErrorCode.FileServerInUse, ex.Code);

        // 引用它的文件路径删掉之后(软删)就不再挡着
        await paths.DeleteAsync(path.Id);
        await service.DeleteAsync(id);
        var gone = await Rejected(service.GetAsync(id));
        Assert.Equal(ErrorCode.FileServerNotFound, gone.Code);
    }

    [Fact]
    public async Task Page_filters_by_keyword_and_enabled_and_options_list_all()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFileServerService>();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var on = await service.AddAsync(new FileServerSaveInput($"on-{tag}", env.Root, null, null, true, null));
        var off = await service.AddAsync(new FileServerSaveInput($"off-{tag}", env.Root, null, null, false, null));

        var byKeyword = await service.PageAsync(new FileServerPageInput { Keyword = tag });
        Assert.Equal(new[] { on, off }.Order(), byKeyword.Items.Select(v => v.Id).Order());

        var enabledOnly = await service.PageAsync(new FileServerPageInput { Keyword = tag, Enabled = true });
        Assert.Equal([on], enabledOnly.Items.Select(v => v.Id));

        var options = await service.OptionsAsync();
        Assert.Contains(options, o => o.Id == off && !o.Enabled && o.Name == $"off-{tag}");
        Assert.Contains(options, o => o.Id == on && o.Enabled && o.RootPath == env.Root);
    }

    [Fact]
    public async Task Mutations_invalidate_server_cache()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFileServerService>();
        var cache = env.Service<ICacheProvider>();

        async Task Prime() => await cache.SetAsync(CacheKeys.FileServers(), "stale");
        async Task AssertEvicted(string step) =>
            Assert.True(await cache.GetAsync<string>(CacheKeys.FileServers()) is null, $"{step}之后缓存应已失效");

        await Prime();
        var name = NewName();
        var id = await service.AddAsync(Input(name, env.Root));
        await AssertEvicted("新增");

        await Prime();
        await service.UpdateAsync(id, Input(name, env.Root, null, null));
        await AssertEvicted("修改");

        await Prime();
        await service.DeleteAsync(id);
        await AssertEvicted("删除");
    }

    // ── 同一主机的账号与密码一起改 ─────────────────────────────────────

    /// <summary>nas01 上两行(files、scan),nas02 上一行;账号密码各自一致。</summary>
    private static async Task<(long Files, long Scan, long Other)> HostRowsAsync(FilePathTestEnv env)
    {
        var service = env.Service<IFileServerService>();
        var files = await service.AddAsync(Input("files", "//nas01/files", "svc-files", "p@ss"));
        var scan = await service.AddAsync(Input("scan", "//nas01/scan", "svc-files", "p@ss"));
        var other = await service.AddAsync(Input("other", "//nas02/files", "svc-other", "x"));
        return (files, scan, other);
    }

    private static FilePathTestEnv HostEnv(Action<IServiceCollection>? overrides = null) =>
        FilePathTestEnv.Create(supportsCredentials: true, overrides: overrides, extraRoots: ["//nas01/files", "//nas01/scan", "//nas02/files"]);

    private static async Task<(string? UserName, string? Envelope)> CredentialAsync(FilePathTestEnv env, long id)
    {
        var row = await RowAsync(env, id);
        return (row.UserName, row.PasswordProtected);
    }

    [Fact]
    public async Task Changing_account_updates_every_row_on_the_same_host()
    {
        await using var env = HostEnv();
        var (files, scan, other) = await HostRowsAsync(env);
        var otherBefore = await CredentialAsync(env, other);

        await env.Service<IFileServerService>().UpdateAsync(files, Input("files", "//nas01/files", "svc-new", "n3w"));

        var a = await CredentialAsync(env, files);
        Assert.Equal(("svc-new", a.Envelope), await CredentialAsync(env, scan));
        Assert.Equal("n3w", env.Service<ISecretProtector>().Unprotect(a.Envelope!));
        // 别的主机不动
        Assert.Equal(otherBefore, await CredentialAsync(env, other));
    }

    [Fact]
    public async Task Changing_only_the_password_updates_every_row_on_the_same_host()
    {
        await using var env = HostEnv();
        var (files, scan, _) = await HostRowsAsync(env);

        await env.Service<IFileServerService>().UpdateAsync(scan, Input("scan", "//nas01/scan", "svc-files", "n3w"));

        var b = await CredentialAsync(env, scan);
        Assert.Equal(("svc-files", b.Envelope), await CredentialAsync(env, files));
        Assert.Equal("n3w", env.Service<ISecretProtector>().Unprotect(b.Envelope!));
    }

    [Fact]
    public async Task Account_case_change_updates_every_row_on_the_same_host()
    {
        await using var env = HostEnv();
        var (files, scan, _) = await HostRowsAsync(env);

        // 只差大小写也一起改,同一台主机上各行的写法始终一致
        await env.Service<IFileServerService>().UpdateAsync(files, Input("files", "//nas01/files", "SVC-Files", null));

        var a = await CredentialAsync(env, files);
        Assert.Equal("SVC-Files", a.UserName);
        Assert.Equal(a, await CredentialAsync(env, scan));
    }

    [Fact]
    public async Task Clearing_account_clears_every_row_on_the_same_host()
    {
        await using var env = HostEnv();
        var (files, scan, other) = await HostRowsAsync(env);

        await env.Service<IFileServerService>().UpdateAsync(files, Input("files", "//nas01/files", null, null));

        Assert.Equal((null, null), await CredentialAsync(env, files));
        Assert.Equal((null, null), await CredentialAsync(env, scan));
        Assert.Equal("svc-other", (await CredentialAsync(env, other)).UserName);
    }

    [Fact]
    public async Task Renaming_does_not_touch_other_rows()
    {
        await using var env = HostEnv();
        var (files, scan, _) = await HostRowsAsync(env);
        var before = await RowAsync(env, scan);

        await env.Service<IFileServerService>().UpdateAsync(files, Input("files-2", "//nas01/files", "svc-files", null));

        var after = await RowAsync(env, scan);
        Assert.Equal((before.UserName, before.PasswordProtected, before.UpdateTime), (after.UserName, after.PasswordProtected, after.UpdateTime));
    }

    [Fact]
    public async Task Moving_to_another_host_with_a_different_account_is_still_rejected()
    {
        await using var env = HostEnv();
        var (files, scan, other) = await HostRowsAsync(env);
        var service = env.Service<IFileServerService>();

        // 换到 nas02:那台主机上已经是 svc-other,不替那台主机改账号
        var moved = await Rejected(service.UpdateAsync(files, Input("files", "//nas02/files", "svc-files", "p@ss")));
        Assert.Equal(ErrorCode.FileServerAccountConflict, moved.Code);
        Assert.Equal("svc-other", (await CredentialAsync(env, other)).UserName);

        // 新增也不级联
        var added = await Rejected(service.AddAsync(Input(NewName(), "//nas01/files", "svc-b", "p@ss")));
        Assert.Equal(ErrorCode.FileServerAccountConflict, added.Code);
        Assert.Equal("svc-files", (await CredentialAsync(env, scan)).UserName);
    }

    [Fact]
    public async Task Cascade_invalidates_the_resolver_cache_for_the_other_rows()
    {
        await using var env = HostEnv();
        var (files, scan, _) = await HostRowsAsync(env);
        var (_, code) = await env.AddRawPathAsync(scan, null, null);
        var resolver = env.Service<IFilePathResolver>();
        await resolver.ResolveAsync(code);   // 先把整表缓存读热

        await env.Service<IFileServerService>().UpdateAsync(files, Input("files", "//nas01/files", "svc-new", "n3w"));
        await resolver.ResolveAsync(code);

        var target = env.Connector.Calls[^1].Target;
        Assert.Equal((scan, "svc-new"), (target.Id, target.UserName));
        Assert.Equal("n3w", env.Service<ISecretProtector>().Unprotect(target.PasswordProtected!));
    }

    /// <summary>级联写完再失败:本行和级联行的修改都要一起回滚。</summary>
    private sealed class FailingCascadeService(
        IRepository<SysFileServer> servers, IRepository<SysFilePath> paths, IDataProtectionKeyProvider keyProvider,
        ISecretProtector secretProtector, ICacheProvider cache, IFileServerConnector connector, AdminFilePathOptions options,
        ILogger<FileServerService> logger, IFilePathCircuit circuit)
        : FileServerService(servers, paths, keyProvider, secretProtector, cache, connector, options, logger, circuit)
    {
        protected override async Task CascadeHostCredentialAsync(SysFileServer saved, IReadOnlyList<SysFileServer> others)
        {
            await base.CascadeHostCredentialAsync(saved, others);
            throw new InvalidOperationException("级联之后失败");
        }
    }

    [Fact]
    public async Task Failed_cascade_rolls_back_the_whole_update()
    {
        await using var env = HostEnv(s => s.Replace(ServiceDescriptor.Scoped<IFileServerService, FailingCascadeService>()));
        var (files, scan, _) = await HostRowsAsync(env);
        var a = await CredentialAsync(env, files);
        var b = await CredentialAsync(env, scan);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            env.Service<IFileServerService>().UpdateAsync(files, Input("files", "//nas01/files", "svc-new", "n3w")));

        Assert.Equal(a, await CredentialAsync(env, files));
        Assert.Equal(b, await CredentialAsync(env, scan));
    }

    [Fact]
    public async Task Test_connection_does_not_check_same_host_account()
    {
        await using var env = HostEnv();
        var (files, _, _) = await HostRowsAsync(env);

        env.Connector.Fail = new IOException("不去连真的主机");

        // 保存前先试新账号:同主机别的行还是旧账号,测试连接不能因此拒绝
        await env.Service<IFileServerService>().TestAsync(new FileServerTestInput(files, "//nas01/files", "svc-new", "n3w"));

        Assert.Equal("svc-new", env.Connector.Calls[^1].Target.UserName);
    }

    // ── 熔断 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_check_is_not_blocked_by_the_circuit_and_closes_it()
    {
        await using var env = FilePathTestEnv.Create();
        var id = await env.AddServerAsync();
        var circuit = env.Service<IFilePathCircuit>();
        circuit.ReportFailure(id, new TimeoutException());
        Assert.Throws<AdminException>(() => circuit.ThrowIfOpen(id));

        // 在线状态本来就是用来发现服务器恢复的:熔断打开也照样去查,查通了就关闭熔断
        var status = Assert.Single(await env.Service<IFileServerService>().StatusAsync([id]));

        Assert.True(status.Online);
        circuit.ThrowIfOpen(id);
    }

    [Fact]
    public async Task Failed_test_connection_does_not_open_the_circuit()
    {
        await using var env = FilePathTestEnv.Create();
        var id = await env.AddServerAsync();
        env.Connector.Fail = new FileServerConnectException(FileAccessFailure.NetworkPathNotFound, 53);

        // 测的是弹窗里当前填的内容,可能和已保存的不同,不拿它的失败去挡已保存的配置
        var result = await env.Service<IFileServerService>().TestAsync(new FileServerTestInput(id, env.Root, null, null));

        Assert.Equal(FileAccessFailure.NetworkPathNotFound, result.Reason);
        env.Service<IFilePathCircuit>().ThrowIfOpen(id);
    }
}
