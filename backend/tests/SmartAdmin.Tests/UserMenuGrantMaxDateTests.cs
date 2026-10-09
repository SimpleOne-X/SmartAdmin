using System.Text.Json;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>
/// 委派授权的最晚到期日由服务端按自己的本地日期给出:校验与界面展示是同一份计算。
/// 服务器在 UTC、用户在东八区时,每天凌晨浏览器的日期领先服务器一天,浏览器自己加天数会得到一个服务端拒收的上限。
/// </summary>
public class UserMenuGrantMaxDateTests
{
    private const long BizWorkbench = 110;

    /// <summary>固定的本地时钟:本地时区取 UTC,本地时间就等于给定的时间。</summary>
    private sealed class LocalClock(DateTime local) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(local, DateTimeKind.Utc));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FakeUser(bool authenticated, bool superAdmin) : ICurrentUser
    {
        public bool IsAuthenticated => authenticated;
        public long? UserId => authenticated ? 7 : null;
        public string? SessionId => null;
        public bool IsSuperAdmin => superAdmin;
        public long? OrgId => null;
        public string? IpAddress => null;
        public string? UserAgent => null;
    }

    /// <summary>只用到时钟、安全选项和当前用户的策略;其余依赖在这些用例里不会被碰到。</summary>
    private sealed class Policy(AdminSecurityOptions security, TimeProvider time, ICurrentUser? user)
        : UserMenuGrantPolicy(null!, null!, null!, null!, null!, security, time, user)
    {
        public void CheckExpiry(params UserMenuGrantUpsert[] upserts) => EnsureDelegatedExpiry(upserts);
    }

    private static readonly FakeUser Admin = new(authenticated: true, superAdmin: false);

    private static Policy PolicyAt(DateTime local, int maxDays = 90, ICurrentUser? user = null) =>
        new(new AdminSecurityOptions { DelegatedGrantMaxDays = maxDays }, new LocalClock(local), user ?? Admin);

    private static UserMenuGrantUpsert Allow(DateTime? expire) =>
        new() { MenuId = BizWorkbench, Effect = UserMenuEffect.Allow, ExpireTime = expire };

    /// <summary>最晚到期日 = 服务器本地日期 + 天数,与一天中的时刻无关;跨过零点那一秒才进到下一天。</summary>
    [Theory]
    [InlineData("2026-10-09T00:00:00", "2027-01-07")]
    [InlineData("2026-10-09T12:30:00", "2027-01-07")]
    [InlineData("2026-10-09T23:59:59", "2027-01-07")]
    [InlineData("2026-10-10T00:00:00", "2027-01-08")]
    public void Max_date_is_server_local_date_plus_max_days(string now, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), PolicyAt(DateTime.Parse(now)).DelegatedMaxDate);

    /// <summary>天数跟着配置走,跨月跨年照常。</summary>
    [Fact]
    public void Max_date_follows_configured_days()
    {
        Assert.Equal(new DateOnly(2026, 10, 10), PolicyAt(new DateTime(2026, 10, 9, 8, 0, 0), maxDays: 1).DelegatedMaxDate);
        Assert.Equal(new DateOnly(2036, 10, 6), PolicyAt(new DateTime(2026, 10, 9, 8, 0, 0), maxDays: 3650).DelegatedMaxDate);
    }

    /// <summary>不受限(配置 0、超管、无登录上下文)时没有最晚到期日,和最长天数同进退。</summary>
    [Fact]
    public void Max_date_is_null_when_unrestricted()
    {
        var now = new DateTime(2026, 10, 9, 8, 0, 0);

        Assert.Null(PolicyAt(now, maxDays: 0).DelegatedMaxDate);
        Assert.Null(PolicyAt(now, user: new FakeUser(authenticated: true, superAdmin: true)).DelegatedMaxDate);
        Assert.Null(PolicyAt(now, user: new FakeUser(authenticated: false, superAdmin: false)).DelegatedMaxDate);
        Assert.Null(new Policy(new AdminSecurityOptions(), new LocalClock(now), null).DelegatedMaxDate);
        Assert.Equal(90, PolicyAt(now).DelegatedMaxDays);
    }

    /// <summary>校验用的就是展示的那一天:最晚到期日当天的任何时刻放行,晚一天(哪怕只晚一秒进入次日)拒绝。</summary>
    [Theory]
    [InlineData("2027-01-07T00:00:00", true)]
    [InlineData("2027-01-07T23:59:59", true)]
    [InlineData("2027-01-08T00:00:00", false)]
    [InlineData("2027-01-08T23:59:59", false)]
    [InlineData("2026-10-09T08:00:00", true)]
    public void Expiry_check_uses_the_same_date_the_max_date_reports(string expire, bool accepted)
    {
        var policy = PolicyAt(new DateTime(2026, 10, 9, 23, 59, 59));
        var upsert = Allow(DateTime.Parse(expire));

        if (accepted)
        {
            policy.CheckExpiry(upsert);
            return;
        }

        var ex = Assert.Throws<AdminException>(() => policy.CheckExpiry(upsert));
        Assert.Equal(ErrorCode.DelegatedGrantExpiryInvalid, ex.Code);
    }

    /// <summary>零点前后的同一个到期日:23:59:59 时上限是 1 月 7 日,过零点第二天上限是 1 月 8 日,1 月 8 日的到期日才被放行。</summary>
    [Fact]
    public void Same_expiry_date_flips_from_rejected_to_accepted_across_midnight()
    {
        var expire = Allow(new DateTime(2027, 1, 8, 23, 59, 59));

        Assert.Throws<AdminException>(() => PolicyAt(new DateTime(2026, 10, 9, 23, 59, 59)).CheckExpiry(expire));
        PolicyAt(new DateTime(2026, 10, 10, 0, 0, 0)).CheckExpiry(expire);
    }

    /// <summary>「允许」必须带到期时间;「拒绝」不要求;不受限时什么都不查。</summary>
    [Fact]
    public void Allow_needs_expiry_but_deny_and_unrestricted_do_not()
    {
        var now = new DateTime(2026, 10, 9, 8, 0, 0);

        Assert.Throws<AdminException>(() => PolicyAt(now).CheckExpiry(Allow(null)));
        PolicyAt(now).CheckExpiry(new UserMenuGrantUpsert { MenuId = BizWorkbench, Effect = UserMenuEffect.Deny });
        PolicyAt(now, maxDays: 0).CheckExpiry(Allow(null));
    }

    /// <summary>有效权限接口把最晚到期日带给界面:普通管理员是服务器今天 + 90 天的日期串,超管为空。</summary>
    [Fact]
    public async Task Effective_endpoint_carries_the_server_computed_max_date()
    {
        using var f = new AdminAppFactory();
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);

        var before = DateOnly.FromDateTime(DateTime.Now).AddDays(90);
        var asAdmin = await DataAsync(admin, $"/api/v1/sys/user/{target}/menus/effective");
        var after = DateOnly.FromDateTime(DateTime.Now).AddDays(90);   // 夹在零点前后时,两端都算对
        var date = DateOnly.ParseExact(asAdmin.GetProperty("delegatedMaxDate").GetString()!, "yyyy-MM-dd");
        Assert.InRange(date, before, after);
        Assert.Equal(90, asAdmin.GetProperty("delegatedMaxDays").GetInt32());

        var asSuper = await DataAsync(await GrantTestKit.SuperAdminAsync(f), $"/api/v1/sys/user/{target}/menus/effective");
        Assert.Equal(JsonValueKind.Null, asSuper.GetProperty("delegatedMaxDate").ValueKind);
    }

    private static async Task<JsonElement> DataAsync(HttpClient c, string url) =>
        (await (await c.GetAsync(url)).ReadEnvelope()).GetProperty("data");
}
