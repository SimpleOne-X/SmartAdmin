using System.Diagnostics;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>测试连接(测的是表单里当前填的内容,校验和保存相同)与批量在线状态。</summary>
public class FileAccessTestTests
{
    private static string NewName() => $"srv-{Guid.NewGuid():N}";

    private static Task<long> AddReadOnlyPathAsync(FilePathTestEnv env, long serverId, string folder) =>
        env.Service<IFilePathConfigService>().AddAsync(new FilePathAddInput(
            $"fp-{Guid.NewGuid():N}", "只读", serverId, folder, FilePathNamingRule.YearMonthOriginal, FilePathConflictMode.Reject,
            ReadOnly: true, null, null, null, null, true, null));

    [Fact]
    public async Task Server_test_reports_missing_root()
    {
        await using var env = FilePathTestEnv.Create();

        var result = await env.Service<IFileServerService>().TestAsync(
            new FileServerTestInput(null, Path.Combine(env.Root, "missing"), null, null));

        Assert.False(result.Ok);
        Assert.Equal(FileAccessFailure.FolderMissing, result.Reason);
    }

    [Fact]
    public async Task Server_test_succeeds_on_readable_root()
    {
        await using var env = FilePathTestEnv.Create();
        await File.WriteAllTextAsync(Path.Combine(env.Root, "a.txt"), "a");

        var result = await env.Service<IFileServerService>().TestAsync(new FileServerTestInput(null, env.Root, null, null));

        Assert.True(result.Ok, result.Reason);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task Server_test_uses_saved_password_when_blank()
    {
        await using var env = FilePathTestEnv.Create(supportsCredentials: false);
        var service = env.Service<IFileServerService>();
        var id = await service.AddAsync(new FileServerSaveInput(NewName(), env.Root, "svc-files", "p@ss", true, null));
        var saved = (await env.Service<IRepository<SysFileServer>>().GetByIdAsync(id))!.PasswordProtected;

        var result = await service.TestAsync(new FileServerTestInput(id, env.Root, "svc-files", null));

        Assert.True(result.Ok, result.Reason);
        var (target, retryFailed) = Assert.Single(env.Connector.Calls);
        Assert.Equal(saved, target.PasswordProtected);
        Assert.True(retryFailed);
    }

    [Theory]
    [InlineData("//nas02/files", "svc-files", false)]   // 换了主机
    [InlineData("//nas01/files", "svc-other", false)]   // 换了账号
    [InlineData("//nas01/files", "SVC-FILES", true)]    // 账号只差大小写,算同一个
    public async Task Server_test_reuses_saved_password_only_for_same_host_and_account(string root, string userName, bool reused)
    {
        await using var env = FilePathTestEnv.Create(supportsCredentials: true, extraRoots: ["//nas01/files", "//nas02/files"]);
        var service = env.Service<IFileServerService>();
        var id = await service.AddAsync(new FileServerSaveInput(NewName(), "//nas01/files", "svc-files", "p@ss", true, null));
        var saved = (await env.Service<IRepository<SysFileServer>>().GetByIdAsync(id))!.PasswordProtected;
        // 连接器在记录之后就失败,探测不会真去访问这台主机
        env.Connector.Fail = new FileServerConnectException(FileAccessFailure.BadCredentials, 1326);

        await service.TestAsync(new FileServerTestInput(id, root, userName, null));

        var (target, _) = Assert.Single(env.Connector.Calls);
        Assert.Equal(reused ? saved : null, target.PasswordProtected);
    }

    [Fact]
    public async Task Server_test_protects_typed_password_without_saving()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFileServerService>();
        var id = await service.AddAsync(new FileServerSaveInput(NewName(), env.Root, "svc-files", "old", true, null));
        var before = (await env.Service<IRepository<SysFileServer>>().GetByIdAsync(id))!.PasswordProtected;

        await service.TestAsync(new FileServerTestInput(id, env.Root, "svc-files", "typed"));

        var (target, _) = Assert.Single(env.Connector.Calls);
        Assert.Equal("typed", env.Service<ISecretProtector>().Unprotect(target.PasswordProtected!));
        Assert.Equal(before, (await env.Service<IRepository<SysFileServer>>().GetByIdAsync(id))!.PasswordProtected);
    }

    [Fact]
    public async Task Server_test_applies_save_validation()
    {
        await using var env = FilePathTestEnv.Create();

        var ex = await Assert.ThrowsAsync<AdminException>(() => env.Service<IFileServerService>().TestAsync(
            new FileServerTestInput(null, Path.Combine(Path.GetTempPath(), $"smart-fp-outside-{Guid.NewGuid():N}"), null, null)));

        Assert.Equal(ErrorCode.FileServerRootNotAllowed, ex.Code);
        Assert.Empty(env.Connector.Calls);
    }

    [Fact]
    public async Task Server_test_reports_connector_failure_as_reason()
    {
        await using var env = FilePathTestEnv.Create();
        env.Connector.Fail = new FileServerConnectException(FileAccessFailure.BadCredentials, 1326);

        var result = await env.Service<IFileServerService>().TestAsync(new FileServerTestInput(null, env.Root, null, null));

        Assert.False(result.Ok);
        Assert.Equal(FileAccessFailure.BadCredentials, result.Reason);
    }

    [Fact]
    public async Task Path_test_writes_and_removes_probe()
    {
        await using var env = FilePathTestEnv.Create();
        var serverId = await env.AddServerAsync();

        var result = await env.Service<IFilePathConfigService>().TestAsync(new FilePathTestInput(serverId, @"Out\Sub", ReadOnly: false));

        Assert.True(result.Ok, result.Reason);
        var folder = Path.Combine(env.Root, "Out", "Sub");
        Assert.True(Directory.Exists(folder));
        Assert.Empty(Directory.EnumerateFiles(folder, ".smartadmin-probe-*"));
        var (_, retryFailed) = Assert.Single(env.Connector.Calls);
        Assert.True(retryFailed);
    }

    [Fact]
    public async Task Read_only_path_test_only_checks_existence()
    {
        await using var env = FilePathTestEnv.Create();
        var serverId = await env.AddServerAsync();

        var result = await env.Service<IFilePathConfigService>().TestAsync(new FilePathTestInput(serverId, "Nope", ReadOnly: true));

        Assert.False(result.Ok);
        Assert.Equal(FileAccessFailure.FolderMissing, result.Reason);
        Assert.False(Directory.Exists(Path.Combine(env.Root, "Nope")));
    }

    [Fact]
    public async Task Path_test_rejects_bad_folder()
    {
        await using var env = FilePathTestEnv.Create();
        var serverId = await env.AddServerAsync();
        var service = env.Service<IFilePathConfigService>();

        var invalid = await Assert.ThrowsAsync<AdminException>(() => service.TestAsync(new FilePathTestInput(serverId, "../x", false)));
        var noServer = await Assert.ThrowsAsync<AdminException>(() => service.TestAsync(new FilePathTestInput(-1, "Docs", false)));

        Assert.Equal(ErrorCode.FilePathInvalidPath, invalid.Code);
        Assert.Equal(ErrorCode.FileServerNotFound, noServer.Code);
        Assert.Empty(env.Connector.Calls);
    }

    [Fact]
    public async Task Test_times_out_after_5_seconds()
    {
        await using var env = FilePathTestEnv.Create();
        env.Connector.Hang = true;
        var watch = Stopwatch.StartNew();

        var result = await env.Service<IFileServerService>().TestAsync(new FileServerTestInput(null, env.Root, null, null));

        Assert.False(result.Ok);
        Assert.Equal(FileAccessFailure.Timeout, result.Reason);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(7), $"耗时 {watch.Elapsed}");
        // 到时限后连接器收到的令牌被取消:排在同一共享连接锁后面的探测随之退出,不在后台一个接一个地去连
        Assert.True(env.Connector.LastToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Status_reports_each_row()
    {
        await using var env = FilePathTestEnv.Create();
        var serverId = await env.AddServerAsync();
        var missingServerId = await env.Service<IFileServerService>().AddAsync(
            new FileServerSaveInput(NewName(), Path.Combine(env.Root, "gone"), null, null, true, null));
        Directory.CreateDirectory(Path.Combine(env.Root, "Here"));
        var present = await AddReadOnlyPathAsync(env, serverId, "Here");
        var absent = await AddReadOnlyPathAsync(env, serverId, "Gone");

        var paths = await env.Service<IFilePathConfigService>().StatusAsync([present, absent, 404]);
        var servers = await env.Service<IFileServerService>().StatusAsync([serverId, missingServerId, 404]);

        Assert.Equal([(present, true, (string?)null), (absent, false, FileAccessFailure.FolderMissing)],
            paths.Select(s => (s.Id, s.Online, s.Reason)));
        Assert.Equal([(serverId, true, (string?)null), (missingServerId, false, FileAccessFailure.FolderMissing)],
            servers.Select(s => (s.Id, s.Online, s.Reason)));
        // 在线状态不重试已记下的失败
        Assert.All(env.Connector.Calls, c => Assert.False(c.RetryFailed));
    }
}
