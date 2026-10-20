using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
}
