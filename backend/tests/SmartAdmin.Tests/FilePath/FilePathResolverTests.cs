using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>文件路径解析:编码 → 启用的行 + 启用的服务器 → 白名单复检 → 连接器 → 文件夹完整路径;配置变更后缓存即时失效。</summary>
public class FilePathResolverTests
{
    private static string NewCode() => $"r-{Guid.NewGuid():N}";

    private static async Task<(long Id, string Code)> AddPathAsync(
        FilePathTestEnv env, long serverId, string? folder = "Docs", bool enabled = true,
        string? extensions = ".pdf", IReadOnlyList<long>? upload = null)
    {
        var code = NewCode();
        var id = await env.Service<IFilePathConfigService>().AddAsync(new FilePathAddInput(
            code, "订单附件", serverId, folder, FilePathNamingRule.YearMonthOriginal, FilePathConflictMode.Reject,
            false, extensions, 10, upload, null, enabled, null));
        return (id, code);
    }

    private static async Task<AdminException> Rejected(Task task) => await Assert.ThrowsAsync<AdminException>(() => task);

    [Fact]
    public async Task Resolve_returns_folder_under_server_root()
    {
        await using var env = FilePathTestEnv.Create();
        var serverId = await env.AddServerAsync();
        var (_, code) = await AddPathAsync(env, serverId, "Docs");
        var (_, rootCode) = await AddPathAsync(env, serverId, folder: null);
        var resolver = env.Service<IFilePathResolver>();

        var target = await resolver.ResolveAsync(code);
        Assert.Equal(Path.Combine(env.Root, "Docs"), target.FolderFullPath);
        Assert.Equal(code, target.Rule.Code);

        // 文件夹为空 = 根路径本身
        Assert.Equal(Path.GetFullPath(env.Root), (await resolver.ResolveAsync(rootCode)).FolderFullPath);
    }

    [Fact]
    public async Task GetRule_maps_row_and_server_without_touching_connector()
    {
        await using var env = FilePathTestEnv.Create();
        var serverId = await env.AddServerAsync();
        var (id, code) = await AddPathAsync(env, serverId, "Docs", extensions: ".pdf,.png", upload: [7, 3]);

        var rule = await env.Service<IFilePathResolver>().GetRuleAsync(code);

        Assert.Equal(id, rule.Id);
        Assert.Equal(serverId, rule.Server.Id);
        Assert.Equal(env.Root, rule.Server.RootPath);
        Assert.Equal("Docs", rule.Folder);
        Assert.Equal([".pdf", ".png"], rule.AllowedExtensions);
        Assert.Equal(10, rule.MaxSizeMb);
        Assert.Equal([3L, 7L], rule.UploadRoleIds);
        Assert.Empty(rule.DownloadRoleIds);
        Assert.Empty(env.Connector.Calls);
    }

    [Fact]
    public async Task Disabled_or_missing_code_is_not_found()
    {
        await using var env = FilePathTestEnv.Create();
        var serverId = await env.AddServerAsync();
        var (_, disabled) = await AddPathAsync(env, serverId, enabled: false);
        var resolver = env.Service<IFilePathResolver>();

        Assert.Equal(ErrorCode.FilePathNotFound, (await Rejected(resolver.ResolveAsync("no-such-code"))).Code);
        Assert.Equal(ErrorCode.FilePathNotFound, (await Rejected(resolver.ResolveAsync(disabled))).Code);
        Assert.Equal(ErrorCode.FilePathNotFound, (await Rejected(resolver.GetRuleAsync(disabled))).Code);
    }

    [Fact]
    public async Task Disabled_server_blocks_resolution()
    {
        await using var env = FilePathTestEnv.Create();
        var serverId = await env.AddServerAsync();
        var (_, code) = await AddPathAsync(env, serverId);
        var resolver = env.Service<IFilePathResolver>();
        await resolver.ResolveAsync(code);

        var servers = env.Service<IFileServerService>();
        var view = await servers.GetAsync(serverId);
        await servers.UpdateAsync(serverId, new FileServerSaveInput(view.Name, view.RootPath, null, null, false, null));

        Assert.Equal(ErrorCode.FileServerNotFound, (await Rejected(resolver.ResolveAsync(code))).Code);
    }

    [Fact]
    public async Task Whitelist_is_rechecked_on_read()
    {
        await using var env = FilePathTestEnv.Create();
        var serverId = await env.AddServerAsync();
        var (_, code) = await AddPathAsync(env, serverId);
        var resolver = env.Service<IFilePathResolver>();
        await resolver.ResolveAsync(code);

        // 行已经在缓存里;白名单改小之后仍要拒绝,说明复检不依赖行缓存
        env.Service<AdminFilePathOptions>().AllowedRoots = [Path.Combine(Path.GetTempPath(), $"smart-fp-other-{Guid.NewGuid():N}")];

        Assert.Equal(ErrorCode.FileServerRootNotAllowed, (await Rejected(resolver.ResolveAsync(code))).Code);
    }

    [Fact]
    public async Task Without_allowed_roots_saved_rows_resolve_and_are_rechecked_on_read()
    {
        await using var env = FilePathTestEnv.Create();
        var options = env.Service<AdminFilePathOptions>();
        options.AllowedRoots = [];
        var serverId = await env.AddServerAsync();
        var (_, code) = await AddPathAsync(env, serverId);
        var resolver = env.Service<IFilePathResolver>();
        await resolver.ResolveAsync(code);

        // 暂存目录后来挪进了根路径之下:读取时同样复检
        options.StagingPath = Path.Combine(env.Root, "staging");

        Assert.Equal(ErrorCode.FileServerRootProtected, (await Rejected(resolver.ResolveAsync(code))).Code);
    }

    [Fact]
    public async Task Resolve_calls_connector_before_access()
    {
        await using var env = FilePathTestEnv.Create();
        var serverId = await env.AddServerAsync();
        var (_, code) = await AddPathAsync(env, serverId);

        await env.Service<IFilePathResolver>().ResolveAsync(code);

        var call = Assert.Single(env.Connector.Calls);
        Assert.False(call.RetryFailed);
        Assert.Equal(serverId, call.Target.Id);
        Assert.Equal(env.Root, call.Target.RootPath);
    }

    [Fact]
    public async Task Connector_failure_becomes_folder_unavailable()
    {
        await using var env = FilePathTestEnv.Create();
        var serverId = await env.AddServerAsync();
        var (_, code) = await AddPathAsync(env, serverId);
        env.Connector.Fail = new FileServerConnectException("badCredentials", 1326);

        var ex = await Rejected(env.Service<IFilePathResolver>().ResolveAsync(code));

        Assert.Equal(ErrorCode.FilePathFolderUnavailable, ex.Code);
        Assert.Equal("badCredentials", ex.Args!["reason"]);
        Assert.IsType<FileServerConnectException>(ex.InnerException);
    }

    [Fact]
    public async Task Config_change_invalidates_cache()
    {
        await using var env = FilePathTestEnv.Create();
        var serverId = await env.AddServerAsync();
        var (id, code) = await AddPathAsync(env, serverId, "Docs");
        var resolver = env.Service<IFilePathResolver>();
        Assert.Equal(Path.Combine(env.Root, "Docs"), (await resolver.ResolveAsync(code)).FolderFullPath);

        var config = env.Service<IFilePathConfigService>();
        var view = await config.GetAsync(id);
        await config.UpdateAsync(id, new FilePathUpdateInput(
            view.Name, view.ServerId, "Docs2", view.NamingRule, view.OnConflict, view.ReadOnly, view.AllowedExtensions,
            view.MaxSizeMb, view.UploadRoleIds, view.DownloadRoleIds, view.Enabled, view.Remark));

        Assert.Equal(Path.Combine(env.Root, "Docs2"), (await resolver.ResolveAsync(code)).FolderFullPath);
    }

    [Fact]
    public async Task Combine_normalizes_and_stays_inside_folder()
    {
        await using var env = FilePathTestEnv.Create();
        var serverId = await env.AddServerAsync();
        var (_, code) = await AddPathAsync(env, serverId, "Docs");
        var resolver = env.Service<IFilePathResolver>();
        var target = await resolver.ResolveAsync(code);

        Assert.Equal(Path.Combine(env.Root, "Docs", "a", "b.pdf"), resolver.Combine(target, @"a\b.pdf"));

        foreach (var bad in new[] { "../x.pdf", "", "a.pdf." })
            Assert.Equal(ErrorCode.FilePathInvalidPath, Assert.Throws<AdminException>(() => resolver.Combine(target, bad)).Code);
    }

    [Theory]
    [InlineData("admin", null)]
    [InlineData("3;5", null)]
    [InlineData("3,admin", null)]
    [InlineData(null, "admin")]
    [InlineData(null, "3;5")]
    public async Task Corrupt_role_string_makes_the_row_unusable(string? uploadRoleIds, string? downloadRoleIds)
    {
        await using var env = FilePathTestEnv.Create();
        var serverId = await env.AddServerAsync();
        var (_, code) = await env.AddRawPathAsync(serverId, uploadRoleIds, downloadRoleIds);
        var resolver = env.Service<IFilePathResolver>();

        // 角色串坏掉不能退化成「空 = 所有登录用户」;连接器也不该被碰
        Assert.Equal(ErrorCode.FilePathRuleIncomplete, (await Rejected(resolver.GetRuleAsync(code))).Code);
        Assert.Equal(ErrorCode.FilePathRuleIncomplete, (await Rejected(resolver.ResolveAsync(code))).Code);
        Assert.Empty(env.Connector.Calls);
    }

    [Fact]
    public async Task Blank_role_string_still_means_no_restriction()
    {
        await using var env = FilePathTestEnv.Create();
        var serverId = await env.AddServerAsync();
        var (_, blank) = await env.AddRawPathAsync(serverId, " ", "");
        var (_, commas) = await env.AddRawPathAsync(serverId, " , ,", ",3,,5, ");
        var resolver = env.Service<IFilePathResolver>();

        var blankRule = await resolver.GetRuleAsync(blank);
        Assert.Empty(blankRule.UploadRoleIds);
        Assert.Empty(blankRule.DownloadRoleIds);

        var commasRule = await resolver.GetRuleAsync(commas);
        Assert.Empty(commasRule.UploadRoleIds);
        Assert.Equal([3L, 5L], commasRule.DownloadRoleIds);
    }

    [Fact]
    public async Task Network_failure_stops_connecting_while_the_circuit_is_open()
    {
        await using var env = FilePathTestEnv.Create();
        var serverId = await env.AddServerAsync();
        var (_, code) = await AddPathAsync(env, serverId);
        env.Connector.Fail = new FileServerConnectException(FileAccessFailure.NetworkPathNotFound, 53);
        var resolver = env.Service<IFilePathResolver>();
        await Rejected(resolver.ResolveAsync(code));

        // 熔断打开:直接报上次的原因,不再去连
        var blocked = await Rejected(resolver.ResolveAsync(code));

        Assert.Equal((ErrorCode.FilePathFolderUnavailable, FileAccessFailure.NetworkPathNotFound), (blocked.Code, blocked.Args!["reason"]));
        Assert.Single(env.Connector.Calls);
    }

    [Fact]
    public async Task Credential_failure_does_not_open_the_circuit()
    {
        await using var env = FilePathTestEnv.Create();
        var serverId = await env.AddServerAsync();
        var (_, code) = await AddPathAsync(env, serverId);
        env.Connector.Fail = new FileServerConnectException(FileAccessFailure.BadCredentials, 1326);
        var resolver = env.Service<IFilePathResolver>();

        await Rejected(resolver.ResolveAsync(code));
        await Rejected(resolver.ResolveAsync(code));

        // 错密码由连接器自己记着不重试,这里不额外挡
        Assert.Equal(2, env.Connector.Calls.Count);
    }
}
