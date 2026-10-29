using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 会话期限随续期顺延:没开绝对寿命时,会话只在「连续 refreshMinutes 没续期」后失效;开了绝对寿命仍封顶。
/// 直接驱动 <see cref="ISessionService"/>,时间走 <see cref="FakeTimeProvider"/>,续期时长取种子配置 refreshMinutes。
/// </summary>
public class SessionSlidingRefreshTests
{
    /// <summary>种子配置 <c>sys.security.session.refreshMinutes</c> = 10080 分钟。</summary>
    private static readonly TimeSpan RefreshTtl = TimeSpan.FromDays(7);

    /// <summary>本地时区固定为 UTC+8:审计 AOP 与服务取本地时间都走 <c>TimeProvider.GetLocalNow()</c>,固定后「写成本地时间」在任何机器时区下都与 UTC 相差 8 小时。</summary>
    private static readonly TimeZoneInfo LocalZone =
        TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");

    private static FakeTimeProvider NewClock()
    {
        return new FakeTimeProvider(DateTimeOffset.UtcNow, LocalZone);
    }

    /// <summary>不开 CookieMode、不配闲置档;<paramref name="absoluteHours"/> 为 0 即不设绝对寿命。</summary>
    private static AdminAppFactory Factory(FakeTimeProvider clock, int absoluteHours = 0)
    {
        return new AdminAppFactory
        {
            Settings = absoluteHours > 0
                ? new Dictionary<string, string?> { ["SmartAdmin:Security:Session:AbsoluteHours"] = absoluteHours.ToString() }
                : null,
            Overrides = services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            },
        };
    }

    /// <summary>为超管开一个会话:访问令牌 15 分钟,刷新令牌 7 天。</summary>
    private static async Task<(string SessionId, TokenPair Pair)> OpenSessionAsync(IServiceProvider sp)
    {
        var user = await sp.GetRequiredService<IRepository<SysUser>>().GetFirstAsync(u => u.Account == "superAdmin");
        Assert.NotNull(user);
        var sid = Guid.CreateVersion7().ToString("N");
        var pair = sp.GetRequiredService<ITokenProvider>().Create(
            new TokenSubject(user!.Id, user.Account, sid, user.IsSuperAdmin, user.OrgId),
            TimeSpan.FromMinutes(15), RefreshTtl);
        await sp.GetRequiredService<ISessionService>().OpenAsync(user, sid, pair);
        return (sid, pair);
    }

    [Fact]
    public async Task Refresh_without_absolute_hours_slides_the_deadline()
    {
        var clock = NewClock();
        using var f = Factory(clock);
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var sessions = sp.GetRequiredService<ISessionService>();
        var (sid, pair) = await OpenSessionAsync(sp);

        clock.Advance(TimeSpan.FromDays(6));
        var first = await sessions.RefreshAsync(pair.RefreshToken);
        clock.Advance(TimeSpan.FromDays(6));
        var second = await sessions.RefreshAsync(first.Pair.RefreshToken);   // 距登录 12 天,超过单个刷新有效期

        var row = await sp.GetRequiredService<IRepository<SysSession>>().GetFirstAsync(s => s.SessionId == sid);
        Assert.NotNull(row);
        var expected = clock.GetUtcNow().UtcDateTime.Add(RefreshTtl);
        Assert.InRange(row!.AbsoluteExpiresAt, expected.AddSeconds(-2), expected.AddSeconds(2));
        Assert.InRange(row.ExpiresAt, expected.AddSeconds(-2), expected.AddSeconds(2));
        Assert.InRange(second.Pair.RefreshExpiresAt.UtcDateTime, expected.AddSeconds(-2), expected.AddSeconds(2));
        Assert.True(await sessions.IsActiveAsync(sid));
    }

    [Fact]
    public async Task Session_expires_when_not_refreshed_within_refresh_ttl()
    {
        var clock = NewClock();
        using var f = Factory(clock);
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var sessions = sp.GetRequiredService<ISessionService>();
        var (sid, pair) = await OpenSessionAsync(sp);

        clock.Advance(TimeSpan.FromDays(6));
        var refreshed = await sessions.RefreshAsync(pair.RefreshToken);

        // 距登录已满 12 天、距上次续期还差 1 分钟到期:期限从续期时刻算,仍在线
        clock.Advance(RefreshTtl - TimeSpan.FromMinutes(1));
        Assert.True(await sessions.IsActiveAsync(sid));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(await sessions.IsActiveAsync(sid));
        var ex = await Assert.ThrowsAsync<AdminException>(() => sessions.RefreshAsync(refreshed.Pair.RefreshToken));
        Assert.Equal(ErrorCode.RefreshTokenInvalid, ex.Code);
    }

    [Fact]
    public async Task Absolute_hours_still_caps_the_session()
    {
        var clock = NewClock();
        using var f = Factory(clock, absoluteHours: 8);
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var sessions = sp.GetRequiredService<ISessionService>();
        var (sid, pair) = await OpenSessionAsync(sp);
        var cap = clock.GetUtcNow().UtcDateTime.AddHours(8);

        clock.Advance(TimeSpan.FromHours(7));
        var refreshed = await sessions.RefreshAsync(pair.RefreshToken);

        // 绝对期限从登录时刻算,续期不顺延
        var row = await sp.GetRequiredService<IRepository<SysSession>>().GetFirstAsync(s => s.SessionId == sid);
        Assert.NotNull(row);
        Assert.InRange(row!.AbsoluteExpiresAt, cap.AddSeconds(-2), cap.AddSeconds(2));

        clock.Advance(TimeSpan.FromHours(2));

        Assert.False(await sessions.IsActiveAsync(sid));
        var ex = await Assert.ThrowsAsync<AdminException>(() => sessions.RefreshAsync(refreshed.Pair.RefreshToken));
        Assert.Equal(ErrorCode.RefreshTokenInvalid, ex.Code);
    }

    [Fact]
    public async Task Replaying_a_just_rotated_token_is_rejected_without_revoking()
    {
        var clock = NewClock();
        using var f = Factory(clock);
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var sessions = sp.GetRequiredService<ISessionService>();
        var (sid, pair) = await OpenSessionAsync(sp);

        var rotatedAt = clock.GetUtcNow().UtcDateTime;
        var rotated = await sessions.RefreshAsync(pair.RefreshToken);
        clock.Advance(TimeSpan.FromSeconds(5));
        var ex = await Assert.ThrowsAsync<AdminException>(() => sessions.RefreshAsync(pair.RefreshToken));

        // 轮换时刻按 UTC 落库(审计 AOP 的本地时间口径不得渗进来),宽限窗才算得准;
        // 钟的本地时区固定为 UTC+8,写成本地时间会差 8 小时而被这条断言抓到
        var used = await sp.GetRequiredService<IRepository<SysRefreshToken>>()
            .GetFirstAsync(t => t.SessionId == sid && t.Status == RefreshTokenStatus.Used);
        Assert.NotNull(used);
        Assert.NotNull(used!.UpdateTime);
        Assert.InRange(used.UpdateTime!.Value, rotatedAt.AddSeconds(-2), rotatedAt.AddSeconds(2));
        Assert.Equal(ErrorCode.RefreshTokenSuperseded, ex.Code);
        Assert.True(await sessions.IsActiveAsync(sid));
        await sessions.RefreshAsync(rotated.Pair.RefreshToken);   // 新令牌仍可续期,说明会话没被吊销
    }

    [Fact]
    public async Task Replaying_after_the_grace_window_still_revokes_the_session()
    {
        var clock = NewClock();
        using var f = Factory(clock);
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var sessions = sp.GetRequiredService<ISessionService>();
        var (sid, pair) = await OpenSessionAsync(sp);

        await sessions.RefreshAsync(pair.RefreshToken);
        clock.Advance(TimeSpan.FromSeconds(11));
        var ex = await Assert.ThrowsAsync<AdminException>(() => sessions.RefreshAsync(pair.RefreshToken));

        Assert.Equal(ErrorCode.RefreshTokenInvalid, ex.Code);
        Assert.False(await sessions.IsActiveAsync(sid));
    }

    // SQLite 下两次调用可能被串行化:后到的那次读到已是 Used 且在宽限窗内,走宽限分支同样得 40037,
    // 此时 rotated == 0 那条路径不一定被走到;两条路径对外表现一致,断言只锁结果
    [Fact]
    public async Task Losing_a_concurrent_rotation_reports_superseded()
    {
        var clock = NewClock();
        using var f = Factory(clock);
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var (sid, pair) = await OpenSessionAsync(sp);

        var outcomes = await Task.WhenAll(
            TryRefreshAsync(f, pair.RefreshToken),
            TryRefreshAsync(f, pair.RefreshToken));

        Assert.Single(outcomes, e => e is null);
        var loser = Assert.Single(outcomes, e => e is not null);
        Assert.Equal(ErrorCode.RefreshTokenSuperseded, loser!.Code);
        Assert.True(await sp.GetRequiredService<ISessionService>().IsActiveAsync(sid));
    }

    /// <summary>各自开 scope(各自一条数据库连接)并发续期;成功返回 null,业务失败返回异常。</summary>
    private static Task<AdminException?> TryRefreshAsync(AdminAppFactory f, string refreshToken)
    {
        return Task.Run(async () =>
        {
            using var scope = f.Services.CreateScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<ISessionService>().RefreshAsync(refreshToken);
                return (AdminException?)null;
            }
            catch (AdminException ex)
            {
                return ex;
            }
        });
    }
}
