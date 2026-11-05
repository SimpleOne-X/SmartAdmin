using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlSugar;
using SmartAdmin.AspNetCore;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// WorkerId 数据库租约守卫——同一 WorkerId 不能被两个<b>活着的</b>实例同时持有,
/// 但被硬杀的上一世留下的残留租约必须能被同机同号的新进程立刻接管(否则开发期每次停止调试都要干等一个 TTL)。
/// <para>身份(机器名 / pid / pid 是否活着)从构造参数注入,测试才有办法模拟"另一台机器"和"已经死掉的前任"。</para>
/// </summary>
public class WorkerIdLeaseGuardTests
{
    /// <param name="createLeaseTable">建好 sys_worker_lease。宿主里由 DatabaseInitializer 建表,这里没有它,自己建。</param>
    private static (ServiceProvider Sp, string DbFile) BuildProvider(bool createLeaseTable = true)
    {
        var id = $"wlg-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new AdminIdOptions { WorkerId = 0 });
        services.AddSingleton(new AdminJobsOptions());
        services.AddSmartAdminSqlSugar(new AdminDatabaseOptions
        {
            DbType = TestDb.DbType,
            ConnectionString = TestDb.ConnectionString(id, dbFile),
        });
        var sp = services.BuildServiceProvider();
        if (createLeaseTable) sp.GetRequiredService<ISqlSugarClient>().CodeFirst.InitTables<SysWorkerLease>();
        return (sp, dbFile);
    }

    /// <param name="alive">"库里那行租约的 pid 还活着吗"的回答。</param>
    private static WorkerIdLeaseGuard NewGuard(IServiceProvider sp, ISqlSugarClient db, string machine, int pid, bool alive = true)
        => NewGuard(sp, db, machine, pid, _ => alive);

    private static WorkerIdLeaseGuard NewGuard(IServiceProvider sp, ISqlSugarClient db, string machine, int pid,
        Func<int, bool> isAlive, TimeProvider? time = null)
        => new(db,
            new AdminIdOptions { WorkerId = 0 },
            new AdminJobsOptions(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<WorkerIdLeaseGuard>(),
            time,
            identity: new WorkerLeaseIdentity(machine, pid, isAlive));

    /// <summary>直接写一行"本机 pid=100 的旧进程持有 0 号、租约还有一天"。模拟的是活着但不归测试管的旧进程,没有续租循环。</summary>
    private static Task InsertLiveLease(ISqlSugarClient db, string machine, int pid)
        => db.Insertable(new SysWorkerLease
        {
            WorkerId = 0,
            NodeName = $"{machine}#0@oldproc",
            Pid = pid,
            LeaseExpiresAt = DateTime.Now.AddDays(1),
        }).ExecuteCommandAsync();

    /// <summary>守卫在轮询里多转几圈(每圈真实等 1 秒),确认它确实停在"等"上,而不是已经返回或已经抛了。</summary>
    private static async Task AssertStillWaiting(Task start)
    {
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.False(start.IsCompleted, $"守卫应仍在等旧进程退出,实际状态:{start.Status} {start.Exception?.GetBaseException().Message}");
    }

    [Fact]
    public async Task Second_instance_on_another_machine_throws_without_waiting()
    {
        var (sp, dbFile) = BuildProvider();
        await using (sp)
        {
            var db = sp.GetRequiredService<ISqlSugarClient>();
            var guard1 = NewGuard(sp, db, "host-a", 100);
            await guard1.StartAsync(CancellationToken.None);

            // 另一台机器配了同一个机器号,连的还是同一个库——真冲突,当场拦。
            // 别的机器上的进程不会因为本机等一会儿就让出号来,等只会把报错推迟;10 秒内没抛就说明它在等。
            var guard2 = NewGuard(sp, db, "host-b", 200);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => guard2.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("WorkerId", ex.Message);
            Assert.Contains("host-a#0", ex.Message);

            await guard1.StopAsync(CancellationToken.None);
            guard1.Dispose();
            guard2.Dispose();
        }

        TestDb.Cleanup(dbFile, dbFile);
    }

    /// <summary>
    /// IIS 重叠回收 / 影子复制发布:新进程起来时旧进程还在排空请求,正常停机后会删掉自己的租约行。
    /// 新进程不报错,等到那行消失再拿号。
    /// </summary>
    [Fact]
    public async Task Same_machine_live_process_is_waited_for_until_it_releases_the_lease()
    {
        var (sp, dbFile) = BuildProvider();
        await using (sp)
        {
            var db = sp.GetRequiredService<ISqlSugarClient>();
            var oldGuard = NewGuard(sp, db, "host-a", 100);
            await oldGuard.StartAsync(CancellationToken.None);

            var newGuard = NewGuard(sp, db, "host-a", 200, alive: true);
            var start = newGuard.StartAsync(CancellationToken.None);
            await AssertStillWaiting(start);

            await oldGuard.StopAsync(CancellationToken.None);   // 旧进程正常退出,删掉租约
            oldGuard.Dispose();
            await start.WaitAsync(TimeSpan.FromSeconds(10));

            var row = await db.Queryable<SysWorkerLease>().Where(l => l.WorkerId == 0).FirstAsync();
            Assert.Equal(200, row.Pid);
            Assert.StartsWith("host-a#0@", row.NodeName);

            await newGuard.StopAsync(CancellationToken.None);
            newGuard.Dispose();
        }

        TestDb.Cleanup(dbFile, dbFile);
    }

    /// <summary>旧进程排空超时被 IIS 强杀:租约行留在库里没到期,但 pid 没了。新进程等到它没了就接管,不等 TTL。</summary>
    [Fact]
    public async Task Same_machine_live_process_is_waited_for_until_it_exits()
    {
        var (sp, dbFile) = BuildProvider();
        await using (sp)
        {
            var db = sp.GetRequiredService<ISqlSugarClient>();
            await InsertLiveLease(db, "host-a", 100);

            var oldAlive = true;
            var newGuard = NewGuard(sp, db, "host-a", 200, pid => pid == 100 && Volatile.Read(ref oldAlive));
            var start = newGuard.StartAsync(CancellationToken.None);
            await AssertStillWaiting(start);

            Volatile.Write(ref oldAlive, false);   // 旧进程被强杀,没来得及删租约
            await start.WaitAsync(TimeSpan.FromSeconds(10));

            var row = await db.Queryable<SysWorkerLease>().Where(l => l.WorkerId == 0).FirstAsync();
            Assert.Equal(200, row.Pid);

            await newGuard.StopAsync(CancellationToken.None);
            newGuard.Dispose();
        }

        TestDb.Cleanup(dbFile, dbFile);
    }

    /// <summary>
    /// 同机另一个进程一直不退:多半是同机两份部署配了同一个号,不是回收。等到上限仍在运行就拒绝启动,
    /// 报错里点名 pid,让人知道去看哪个进程。
    /// </summary>
    [Fact]
    public async Task Same_machine_live_process_that_never_exits_fails_after_the_wait_limit()
    {
        var (sp, dbFile) = BuildProvider();
        await using (sp)
        {
            var db = sp.GetRequiredService<ISqlSugarClient>();
            await InsertLiveLease(db, "host-a", 100);

            var clock = new FakeTimeProvider(DateTimeOffset.Now);
            var newGuard = NewGuard(sp, db, "host-a", 200, _ => true, clock);
            var start = newGuard.StartAsync(CancellationToken.None);
            await AssertStillWaiting(start);

            clock.Advance(TimeSpan.FromHours(1));   // 远超等待上限;租约行到明天才过期,所以仍算"被持有"
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => start.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("仍在运行", ex.Message);
            Assert.Contains("pid=100", ex.Message);

            var row = await db.Queryable<SysWorkerLease>().Where(l => l.WorkerId == 0).FirstAsync();
            Assert.Equal(100, row.Pid);   // 没抢到就不能动别人的行
            newGuard.Dispose();
        }

        TestDb.Cleanup(dbFile, dbFile);
    }

    /// <summary>等待期间宿主停机(IIS 放弃这次启动):别一直等到上限,立刻退出。</summary>
    [Fact]
    public async Task Waiting_is_cancelled_when_the_host_stops_starting()
    {
        var (sp, dbFile) = BuildProvider();
        await using (sp)
        {
            var db = sp.GetRequiredService<ISqlSugarClient>();
            await InsertLiveLease(db, "host-a", 100);

            using var cts = new CancellationTokenSource();
            var newGuard = NewGuard(sp, db, "host-a", 200, alive: true);
            var start = newGuard.StartAsync(cts.Token);
            await AssertStillWaiting(start);

            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(10)));
            newGuard.Dispose();
        }

        TestDb.Cleanup(dbFile, dbFile);
    }

    /// <summary>
    /// 守卫要在除建表之外的所有后台服务之前跑完。它在等旧进程退出的那几十秒里,排在前面的服务已经开工:
    /// 任务调度器的节点名就是 <c>{机器名}#{机器号}</c>,和旧进程一模一样,能直接认领主节点、和旧进程一起派发任务,
    /// 用同一个号发 Id——等待本来要防的事就在等待期间发生了。建表必须在它前面,它要读租约表。
    /// </summary>
    [Fact]
    public void Guard_is_the_first_hosted_service_after_the_database_initializer()
    {
        var settings = new Dictionary<string, string?>
        {
            ["SmartAdmin:Database:DbType"] = "Sqlite",
            ["SmartAdmin:Database:ConnectionString"] = "Data Source=:memory:",
            ["SmartAdmin:Jwt:SecretKey"] = "smart-worker-lease-order-test-signing-key-32plus",
            ["SmartAdmin:Id:WorkerId"] = "3",
        };
        var api = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        api.Configuration.Sources.Clear();
        api.Configuration.AddInMemoryCollection(settings);
        api.Services.AddSmartAdmin(api.Configuration);
        AssertGuardRightAfterInitializer(api.Services);

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var worker = new ServiceCollection();
        worker.AddLogging();
        worker.AddSmartAdminWorker(config);
        AssertGuardRightAfterInitializer(worker);

        static void AssertGuardRightAfterInitializer(IServiceCollection services)
        {
            var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
            var init = hosted.FindIndex(d => d.ImplementationType == typeof(DatabaseInitializer));
            var guard = hosted.FindIndex(d => d.ImplementationType == typeof(WorkerIdLeaseGuard));
            Assert.True(init >= 0, "没找到 DatabaseInitializer");
            Assert.Equal(init + 1, guard);
        }
    }

    [Fact]
    public async Task Same_machine_dead_process_lease_is_taken_over()
    {
        var (sp, dbFile) = BuildProvider();
        await using (sp)
        {
            var db = sp.GetRequiredService<ISqlSugarClient>();
            // guard1 拿到租约后被硬杀:租约行留在库里,没到期,StopAsync 没跑过
            var guard1 = NewGuard(sp, db, "host-a", 100);
            await guard1.StartAsync(CancellationToken.None);
            guard1.Dispose();

            // 同机同号重启,前任 pid 已经不在——直接接管,不等 TTL
            var guard2 = NewGuard(sp, db, "host-a", 200, alive: false);
            await guard2.StartAsync(CancellationToken.None);

            var row = await db.Queryable<SysWorkerLease>().Where(l => l.WorkerId == 0).FirstAsync();
            Assert.Equal(200, row.Pid);
            Assert.StartsWith("host-a#0@", row.NodeName);

            await guard2.StopAsync(CancellationToken.None);
            guard2.Dispose();
        }

        TestDb.Cleanup(dbFile, dbFile);
    }

    [Fact]
    public async Task Same_machine_recycled_pid_is_taken_over()
    {
        var (sp, dbFile) = BuildProvider();
        await using (sp)
        {
            var db = sp.GetRequiredService<ISqlSugarClient>();
            // 容器重启:主机名不变,新进程的 pid 又是 1——残留行的 pid 查起来"活着",因为那就是我自己
            var guard1 = NewGuard(sp, db, "pod-0", 1);
            await guard1.StartAsync(CancellationToken.None);
            guard1.Dispose();

            var guard2 = NewGuard(sp, db, "pod-0", 1, alive: true);
            await guard2.StartAsync(CancellationToken.None);

            var row = await db.Queryable<SysWorkerLease>().Where(l => l.WorkerId == 0).FirstAsync();
            Assert.Equal(1, row.Pid);

            await guard2.StopAsync(CancellationToken.None);
            guard2.Dispose();
        }

        TestDb.Cleanup(dbFile, dbFile);
    }

    /// <summary>
    /// 守卫不建表也不改表:建表归 DatabaseInitializer 和它的闸门管。守卫自己跑 CodeFirst 会绕过闸门,
    /// 而且 SQL Server 上每次启动都对已有表删改表注释,只有增删改查权限的账号起不来。缺表时点名报错。
    /// </summary>
    [Fact]
    public async Task Missing_lease_table_fails_with_actionable_message_instead_of_creating_it()
    {
        var (sp, dbFile) = BuildProvider(createLeaseTable: false);
        await using (sp)
        {
            var db = sp.GetRequiredService<ISqlSugarClient>();
            var guard = NewGuard(sp, db, "host-a", 100);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => guard.StartAsync(CancellationToken.None));

            Assert.Contains("sys_worker_lease", ex.Message);
            Assert.Contains("EnableCodeFirstInProduction", ex.Message);
            Assert.False(db.DbMaintenance.IsAnyTable("sys_worker_lease", false));
            guard.Dispose();
        }

        TestDb.Cleanup(dbFile, dbFile);
    }

    [Fact]
    public async Task Stop_releases_lease_allowing_new_instance()
    {
        var (sp, dbFile) = BuildProvider();
        await using (sp)
        {
            var db = sp.GetRequiredService<ISqlSugarClient>();
            var guard1 = NewGuard(sp, db, "host-a", 100);

            await guard1.StartAsync(CancellationToken.None);
            await guard1.StopAsync(CancellationToken.None);
            guard1.Dispose();

            // 正常停机把租约行删了,另一台机器也能立刻接手
            var guard2 = NewGuard(sp, db, "host-b", 200);
            await guard2.StartAsync(CancellationToken.None);
            await guard2.StopAsync(CancellationToken.None);
            guard2.Dispose();
        }

        TestDb.Cleanup(dbFile, dbFile);
    }
}
