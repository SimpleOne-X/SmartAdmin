using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// <see cref="IAiUsageService"/> 的用量查询三件套(Summary/Trend/Page)测试。
/// 不走 HTTP 端点,直接从 DI 解析服务;测试数据用 <see cref="IRepository{TEntity}"/> 直接插 <see cref="SysAiUsageLog"/>,
/// 不走 <c>AiChatClient</c>(批次 2 的桩测试已覆盖网关写入路径)。
/// </summary>
public class AiUsageQueryTests
{
    private static async Task<SysAiUsageLog> SeedAsync(
        IServiceProvider sp,
        string providerCode,
        string model,
        string scene,
        long? userId,
        DateTime createTime,
        int inputTokens,
        int outputTokens,
        bool success,
        int? errorCode = null,
        int latencyMs = 100)
    {
        var repo = sp.GetRequiredService<IRepository<SysAiUsageLog>>();
        var entity = new SysAiUsageLog
        {
            ProviderId = 1,
            ProviderCode = providerCode,
            Model = model,
            Scene = scene,
            UserId = userId,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            TotalTokens = inputTokens + outputTokens,
            UsageSource = 1,
            LatencyMs = latencyMs,
            Success = success,
            ErrorCode = errorCode,
            Streamed = false,
            CreateTime = createTime,   // 显式指定,AOP 只在 CreateTime == default 时才会填当前时间,不会覆盖
        };
        await repo.InsertAsync(entity);
        return entity;
    }

    [Fact]
    public async Task Summary_totals_and_provider_group_are_correct()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var baseTime = new DateTime(2026, 3, 10, 12, 0, 0);

        await SeedAsync(sp, "openai", "gpt-4o", "chat.test", 1001, baseTime, 100, 50, true, latencyMs: 200);
        // 201 而非 300:让均值带小数(200+201)/2=200.5,验证 AvgLatencyMs 没有被静默截断成整数
        await SeedAsync(sp, "openai", "gpt-4o", "chat.test", 1001, baseTime.AddMinutes(5), 200, 100, true, latencyMs: 201);
        await SeedAsync(sp, "anthropic", "claude-3", "chat.other", 1002, baseTime.AddMinutes(10), 50, 25, false, errorCode: 49040, latencyMs: 400);
        await SeedAsync(sp, "anthropic", "claude-3", "chat.other", null, baseTime.AddMinutes(15), 10, 5, false, errorCode: null, latencyMs: 500);

        var usage = sp.GetRequiredService<IAiUsageService>();
        var input = new AiUsageSummaryInput(baseTime.AddHours(-1), baseTime.AddHours(1), null, null, null, null, AiUsageGroupBy.Provider);
        var summary = await usage.SummaryAsync(input);

        Assert.Equal(4, summary.Totals.CallCount);
        Assert.Equal(2, summary.Totals.FailureCount);
        Assert.Equal(360, summary.Totals.InputTokens);    // 100+200+50+10
        Assert.Equal(180, summary.Totals.OutputTokens);   // 50+100+25+5
        Assert.Equal(540, summary.Totals.TotalTokens);
        Assert.Equal(2, summary.Totals.FailuresByErrorCode.Count);
        Assert.Equal(1, summary.Totals.FailuresByErrorCode[49040]);
        Assert.Equal(1, summary.Totals.FailuresByErrorCode[0]);   // ErrorCode 为 null 的失败行归到约定 key 0

        Assert.Equal(2, summary.Groups.Count);
        var openai = Assert.Single(summary.Groups, g => g.Key == "openai");
        Assert.Equal(300, openai.InputTokens);    // 100+200
        Assert.Equal(150, openai.OutputTokens);   // 50+100
        Assert.Equal(2, openai.CallCount);
        Assert.Equal(200.5, openai.AvgLatencyMs);   // (200+201)/2,验证均值不是被截断的整数

        var anthropic = Assert.Single(summary.Groups, g => g.Key == "anthropic");
        Assert.Equal(60, anthropic.InputTokens);   // 50+10
        Assert.Equal(30, anthropic.OutputTokens);  // 25+5
        Assert.Equal(2, anthropic.CallCount);
        Assert.Equal(450, anthropic.AvgLatencyMs); // (400+500)/2

        // openai 份额 450/540*100 = 83.33,anthropic 份额 90/540*100 = 16.67,加起来约等于 100
        var shareSum = summary.Groups.Sum(g => g.SharePercent);
        Assert.True(Math.Abs(shareSum - 100.0) < 0.1, $"份额之和应接近 100,实际 {shareSum}");
        Assert.True(Math.Abs(openai.SharePercent - 83.33) < 0.01);
        Assert.True(Math.Abs(anthropic.SharePercent - 16.67) < 0.01);
    }

    [Fact]
    public async Task Summary_group_by_user_fills_label_for_known_user_and_null_for_system_calls()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        // 后台 DI 作用域没有租户上下文;SysUser 是 ITenantScoped,借 TestTenantContext 让这段建库/
        // 查库过程落到默认租户——下面 SummaryAsync 按 userId 回填 Label 时同样要按租户过滤查得到。
        using var _tenant = TestTenantContext.Use(f.Services, DefaultTenantSeed.DEFAULT_TENANT_ID);

        var users = sp.GetRequiredService<IRepository<SysUser>>();
        var user = new SysUser { Account = $"u-{Guid.NewGuid():N}", Password = "x", Name = "张三" };
        await users.InsertAsync(user);

        var baseTime = new DateTime(2026, 4, 1, 8, 0, 0);
        await SeedAsync(sp, "openai", "gpt-4o", "chat.test", user.Id, baseTime, 100, 100, true);
        await SeedAsync(sp, "openai", "gpt-4o", "chat.test", null, baseTime.AddMinutes(1), 100, 100, true);   // 后台任务,无发起用户

        var usage = sp.GetRequiredService<IAiUsageService>();
        var input = new AiUsageSummaryInput(baseTime.AddHours(-1), baseTime.AddHours(1), null, null, null, null, AiUsageGroupBy.User);
        var summary = await usage.SummaryAsync(input);

        Assert.Equal(2, summary.Groups.Count);
        var userGroup = Assert.Single(summary.Groups, g => g.Key == user.Id.ToString());
        Assert.Equal("张三", userGroup.Label);
        var systemGroup = Assert.Single(summary.Groups, g => g.Key == "0");
        Assert.Null(systemGroup.Label);

        var shareSum = summary.Groups.Sum(g => g.SharePercent);
        Assert.True(Math.Abs(shareSum - 100.0) < 0.1, $"份额之和应接近 100,实际 {shareSum}");
    }

    [Fact]
    public async Task Summary_date_range_is_inclusive_and_excludes_rows_outside()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var from = new DateTime(2026, 7, 10, 0, 0, 0);
        var to = new DateTime(2026, 7, 12, 23, 59, 59);

        await SeedAsync(sp, "openai", "gpt-4o", "s", 1, from, 1, 1, true);               // 恰好落在 From 边界
        await SeedAsync(sp, "openai", "gpt-4o", "s", 1, to, 2, 2, true);                 // 恰好落在 To 边界
        await SeedAsync(sp, "openai", "gpt-4o", "s", 1, from.AddSeconds(-1), 100, 100, true);   // 早于 From
        await SeedAsync(sp, "openai", "gpt-4o", "s", 1, to.AddSeconds(1), 200, 200, true);      // 晚于 To

        var usage = sp.GetRequiredService<IAiUsageService>();
        var summary = await usage.SummaryAsync(new AiUsageSummaryInput(from, to, null, null, null, null, AiUsageGroupBy.Provider));

        Assert.Equal(2, summary.Totals.CallCount);
        Assert.Equal(3, summary.Totals.InputTokens);    // 1+2,区间外的 100/200 不计入
        Assert.Equal(3, summary.Totals.OutputTokens);
    }

    [Fact]
    public async Task Trend_fills_zero_for_days_without_data_and_stays_continuous()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var day1 = new DateTime(2026, 5, 1, 9, 0, 0);
        var day3 = new DateTime(2026, 5, 3, 9, 0, 0);   // 与 day1 同一时刻(9:00),中间隔一天没数据
        await SeedAsync(sp, "openai", "gpt-4o", "s", 1, day1, 100, 50, true);
        await SeedAsync(sp, "openai", "gpt-4o", "s", 1, day3, 20, 10, true);

        var usage = sp.GetRequiredService<IAiUsageService>();
        var trend = await usage.TrendAsync(new AiUsageTrendInput(day1, day3, null, null, null, null));

        Assert.Equal(3, trend.Count);

        Assert.Equal(DateOnly.FromDateTime(day1), trend[0].Date);
        Assert.Equal(100, trend[0].InputTokens);
        Assert.Equal(50, trend[0].OutputTokens);
        Assert.Equal(1, trend[0].CallCount);

        Assert.Equal(DateOnly.FromDateTime(day1.AddDays(1)), trend[1].Date);
        Assert.Equal(0, trend[1].InputTokens);
        Assert.Equal(0, trend[1].OutputTokens);
        Assert.Equal(0, trend[1].CallCount);

        Assert.Equal(DateOnly.FromDateTime(day3), trend[2].Date);
        Assert.Equal(20, trend[2].InputTokens);
        Assert.Equal(10, trend[2].OutputTokens);
        Assert.Equal(1, trend[2].CallCount);
    }

    [Fact]
    public async Task Page_filters_by_dimensions_and_success_then_orders_by_create_time_desc()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var t0 = new DateTime(2026, 6, 1, 10, 0, 0);
        var a = await SeedAsync(sp, "openai", "gpt-4o", "chat.a", 5001, t0, 10, 10, true);
        var b = await SeedAsync(sp, "openai", "gpt-4o", "chat.a", 5001, t0.AddMinutes(1), 10, 10, false, errorCode: 1);
        var c = await SeedAsync(sp, "anthropic", "claude-3", "chat.b", 5002, t0.AddMinutes(2), 10, 10, true);

        var usage = sp.GetRequiredService<IAiUsageService>();

        var byProvider = await usage.PageAsync(new AiUsagePageInput { ProviderCode = "openai" });
        Assert.Equal(2, byProvider.Total);
        Assert.All(byProvider.Items, x => Assert.Equal("openai", x.ProviderCode));
        // CreateTime desc:b (更晚) 在前,a 在后
        Assert.Equal(b.Id, byProvider.Items[0].Id);
        Assert.Equal(a.Id, byProvider.Items[1].Id);

        var successOnly = await usage.PageAsync(new AiUsagePageInput { Success = true });
        Assert.Equal(2, successOnly.Total);   // a, c
        Assert.All(successOnly.Items, x => Assert.True(x.Success));

        var byUser = await usage.PageAsync(new AiUsagePageInput { UserId = 5002 });
        Assert.Equal(1, byUser.Total);
        Assert.Equal(c.Id, byUser.Items[0].Id);

        var byScene = await usage.PageAsync(new AiUsagePageInput { Scene = "chat.b" });
        Assert.Equal(1, byScene.Total);
        Assert.Equal(c.Id, byScene.Items[0].Id);

        var byModel = await usage.PageAsync(new AiUsagePageInput { Model = "claude-3" });
        Assert.Equal(1, byModel.Total);
        Assert.Equal(c.Id, byModel.Items[0].Id);
    }
}
