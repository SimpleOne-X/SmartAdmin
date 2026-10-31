using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 通知接收人解析:真实数据库上的联表查询(绑定表 × 用户表 × 用户角色表)。
/// 子类只为把受保护的解析方法露出来,查询本身用的是默认实现。
/// </summary>
public class NoticeRecipientTests
{
    private sealed class Exposed(IServiceScopeFactory scopes, AdminExternalAuthOptions options, ILogger<NoticeDispatcher> logger)
        : NoticeDispatcher(scopes, options, logger)
    {
        public Task<List<string>> Resolve(IServiceProvider services, NoticeDispatchItem item, string provider) =>
            ResolveSubjectsAsync(services, item, provider, CancellationToken.None);
    }

    private static async Task<List<string>> Resolve(
        AdminAppFactory f, ReceiverType type, IReadOnlyList<long>? targets = null, string provider = "wecom")
    {
        var dispatcher = new Exposed(
            f.Services.GetRequiredService<IServiceScopeFactory>(), new AdminExternalAuthOptions(), NullLogger<NoticeDispatcher>.Instance);
        using var scope = f.Services.CreateScope();
        var subjects = await dispatcher.Resolve(scope.ServiceProvider, new NoticeDispatchItem(1, "t", null, type, targets ?? []), provider);
        return [.. subjects.Order()];
    }

    private static async Task<long> User(AdminAppFactory f, string account, bool enabled = true)
    {
        using var scope = f.Services.CreateScope();
        var user = new SysUser { Account = account, Password = "x", Name = account, Enabled = enabled };
        await scope.ServiceProvider.GetRequiredService<IRepository<SysUser>>().InsertAsync(user);
        return user.Id;
    }

    private static async Task Bind(AdminAppFactory f, long userId, string subject, string provider = "wecom")
    {
        using var scope = f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISysUserExternalService>()
            .BindAsync(userId, new ExternalIdentity(provider, subject));
    }

    private static async Task<long> Member(AdminAppFactory f, long userId, long roleId)
    {
        using var scope = f.Services.CreateScope();
        var link = new SysUserRole { UserId = userId, RoleId = roleId };
        await scope.ServiceProvider.GetRequiredService<IRepository<SysUserRole>>().InsertAsync(link);
        return link.Id;
    }

    [Fact]
    public async Task All_reaches_enabled_bound_users_only()
    {
        using var f = new AdminAppFactory();
        var enabled = await User(f, "enabled");
        var disabled = await User(f, "disabled", enabled: false);
        await User(f, "unbound");
        await Bind(f, enabled, "s-enabled");
        await Bind(f, disabled, "s-disabled");

        Assert.Equal(["s-enabled"], await Resolve(f, ReceiverType.All));
    }

    [Fact]
    public async Task Role_reaches_role_members_once()
    {
        using var f = new AdminAppFactory();
        var both = await User(f, "both");
        var onlyFirst = await User(f, "only-first");
        var outsider = await User(f, "outsider");
        var otherRole = await User(f, "other-role");
        foreach (var (user, subject) in new[] { (both, "s-both"), (onlyFirst, "s-first"), (outsider, "s-out"), (otherRole, "s-other") })
            await Bind(f, user, subject);
        await Member(f, both, 9001);
        await Member(f, both, 9002);
        await Member(f, onlyFirst, 9001);
        await Member(f, otherRole, 9003);

        Assert.Equal(["s-both", "s-first"], await Resolve(f, ReceiverType.Role, [9001, 9002]));
    }

    [Fact]
    public async Task Role_skips_members_who_left_the_role()
    {
        using var f = new AdminAppFactory();
        var stays = await User(f, "stays");
        var left = await User(f, "left");
        await Bind(f, stays, "s-stays");
        await Bind(f, left, "s-left");
        await Member(f, stays, 9001);
        var leftLink = await Member(f, left, 9001);
        using (var scope = f.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IRepository<SysUserRole>>().DeleteAsync(leftLink);

        Assert.Equal(["s-stays"], await Resolve(f, ReceiverType.Role, [9001]));
    }

    [Fact]
    public async Task User_reaches_only_targets()
    {
        using var f = new AdminAppFactory();
        var a = await User(f, "a");
        var b = await User(f, "b");
        var c = await User(f, "c");
        await Bind(f, a, "s-a");
        await Bind(f, b, "s-b");
        await Bind(f, c, "s-c");

        Assert.Equal(["s-a", "s-c"], await Resolve(f, ReceiverType.User, [a, c]));
    }

    [Fact]
    public async Task User_target_that_is_disabled_is_skipped()
    {
        using var f = new AdminAppFactory();
        var off = await User(f, "off", enabled: false);
        await Bind(f, off, "s-off");

        Assert.Empty(await Resolve(f, ReceiverType.User, [off]));
    }

    [Fact]
    public async Task Deleted_user_and_unbound_binding_are_skipped()
    {
        using var f = new AdminAppFactory();
        var deleted = await User(f, "deleted");
        var unbound = await User(f, "unbound");
        var kept = await User(f, "kept");
        await Bind(f, deleted, "s-deleted");
        await Bind(f, unbound, "s-unbound");
        await Bind(f, kept, "s-kept");

        using (var scope = f.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            // 只软删用户、不清绑定:联表上的用户软删过滤必须生效
            await sp.GetRequiredService<IRepository<SysUser>>().DeleteAsync(deleted);
            await sp.GetRequiredService<ISysUserExternalService>().UnbindAsync(unbound, "wecom");
        }

        Assert.Equal(["s-kept"], await Resolve(f, ReceiverType.All));
        Assert.Equal(["s-kept"], await Resolve(f, ReceiverType.User, [deleted, unbound, kept]));
    }

    [Fact]
    public async Task Only_bindings_of_this_provider_count()
    {
        using var f = new AdminAppFactory();
        var user = await User(f, "github-only");
        await Bind(f, user, "gh-1", provider: "github");

        Assert.Empty(await Resolve(f, ReceiverType.All, provider: "wecom"));
        Assert.Equal(["gh-1"], await Resolve(f, ReceiverType.All, provider: "github"));
    }
}
