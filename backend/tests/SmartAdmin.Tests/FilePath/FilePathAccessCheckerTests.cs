using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>文件路径的角色判断:空列表 = 所有登录用户、只认启用的角色、超管直通、未登录拒绝、编码不存在抛未找到。</summary>
public class FilePathAccessCheckerTests
{
    private sealed class FakeUser(long? userId, bool superAdmin = false) : ICurrentUser
    {
        public bool IsAuthenticated => userId is not null;
        public long? UserId => userId;
        public string? SessionId => null;
        public bool IsSuperAdmin => superAdmin;
        public long? OrgId => null;
        public string? IpAddress => null;
        public string? UserAgent => null;
    }

    private static string NewCode() => $"a-{Guid.NewGuid():N}";

    private static async Task<string> AddPathAsync(
        FilePathTestEnv env, IReadOnlyList<long>? upload = null, IReadOnlyList<long>? download = null)
    {
        var code = NewCode();
        await env.Service<IFilePathConfigService>().AddAsync(new FilePathAddInput(
            code, "订单附件", await env.AddServerAsync(), "Docs", FilePathNamingRule.YearMonthOriginal, FilePathConflictMode.Reject,
            false, ".pdf", 10, upload, download, true, null));
        return code;
    }

    private static (FilePathAccessChecker Checker, IServiceScope Scope) NewChecker(FilePathTestEnv env, ICurrentUser user)
    {
        var scope = env.Factory.Services.CreateScope();
        return (ActivatorUtilities.CreateInstance<FilePathAccessChecker>(scope.ServiceProvider, user), scope);
    }

    [Fact]
    public async Task Empty_role_list_allows_any_authenticated_user()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        var (checker, scope) = NewChecker(env, new FakeUser(42));
        using var _ = scope;

        Assert.True(await checker.CanUploadAsync(code));
        Assert.True(await checker.CanDownloadAsync(code));
    }

    [Fact]
    public async Task Unauthenticated_is_denied_even_when_role_list_is_empty()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        var (checker, scope) = NewChecker(env, new FakeUser(null));
        using var _ = scope;

        Assert.False(await checker.CanUploadAsync(code));
        Assert.False(await checker.CanDownloadAsync(code));
    }

    [Fact]
    public async Task Matching_enabled_role_allows()
    {
        await using var env = FilePathTestEnv.Create();
        var roleId = await GrantTestKit.CreateRoleAsync(env.Factory, []);
        var (userId, _) = await GrantTestKit.CreateUserAsync(env.Factory, [roleId]);
        var code = await AddPathAsync(env, upload: [roleId], download: [roleId]);
        var (checker, scope) = NewChecker(env, new FakeUser(userId));
        using var _ = scope;

        Assert.True(await checker.CanUploadAsync(code));
        Assert.True(await checker.CanDownloadAsync(code));
    }

    [Fact]
    public async Task Non_matching_role_denies()
    {
        await using var env = FilePathTestEnv.Create();
        var mine = await GrantTestKit.CreateRoleAsync(env.Factory, []);
        var other = await GrantTestKit.CreateRoleAsync(env.Factory, []);
        var (userId, _) = await GrantTestKit.CreateUserAsync(env.Factory, [mine]);
        // 上传和下载各自独立判断:只在下载角色里的用户不能上传
        var code = await AddPathAsync(env, upload: [other], download: [mine]);
        var (checker, scope) = NewChecker(env, new FakeUser(userId));
        using var _ = scope;

        Assert.False(await checker.CanUploadAsync(code));
        Assert.True(await checker.CanDownloadAsync(code));
    }

    [Fact]
    public async Task Disabled_role_does_not_count()
    {
        await using var env = FilePathTestEnv.Create();
        var roleId = await GrantTestKit.CreateRoleAsync(env.Factory, []);
        var (userId, _) = await GrantTestKit.CreateUserAsync(env.Factory, [roleId]);
        var code = await AddPathAsync(env, upload: [roleId], download: [roleId]);
        var (checker, scope) = NewChecker(env, new FakeUser(userId));
        using var _ = scope;
        Assert.True(await checker.CanUploadAsync(code));

        var roles = env.Service<IRepository<SysRole>>();
        var role = await roles.GetByIdAsync(roleId);
        role!.Enabled = false;
        await roles.UpdateAsync(role);

        Assert.False(await checker.CanUploadAsync(code));
        Assert.False(await checker.CanDownloadAsync(code));
    }

    [Fact]
    public async Task Super_admin_bypasses()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env, upload: [999], download: [999]);
        var (checker, scope) = NewChecker(env, new FakeUser(1, superAdmin: true));
        using var _ = scope;

        Assert.True(await checker.CanUploadAsync(code));
        Assert.True(await checker.CanDownloadAsync(code));
    }

    [Fact]
    public async Task Unknown_code_throws_not_found()
    {
        await using var env = FilePathTestEnv.Create();
        var (checker, scope) = NewChecker(env, new FakeUser(1, superAdmin: true));
        using var _ = scope;

        var up = await Assert.ThrowsAsync<AdminException>(() => checker.CanUploadAsync("no-such-code"));
        var down = await Assert.ThrowsAsync<AdminException>(() => checker.CanDownloadAsync("no-such-code"));

        Assert.Equal(ErrorCode.FilePathNotFound, up.Code);
        Assert.Equal(ErrorCode.FilePathNotFound, down.Code);
    }
}
