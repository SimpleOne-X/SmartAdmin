using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SmartAdmin.Tests;

/// <summary>
/// 超管种子(<c>SuperAdminSeed</c>)的幂等性,在租户隔离全局过滤器硬化之后。
/// <para>种子的幂等检查(<c>HasData</c> 里 <c>users.AsQueryable().Any()</c>)在启动期由 <c>SystemCurrentUser</c>
/// 跑,<c>TenantId</c> 恒为 <c>null</c>。硬化后的租户过滤器要求 <c>currentUser.TenantId != null</c>,若幂等检查
/// 不清掉这层过滤,查询恒回零行——不管超管是否早已建号,每次重启都会误判成"库是空的",重新生成一个随机密码、
/// 再打一次只该在真首启出现一次的横幅(即便这个密码不会真的写进库——见 <c>DatabaseInitializer</c> 按主键判存的
/// upsert 去重——横幅本身出现在不该出现的地方就已经违反了该类的文档承诺)。</para>
/// </summary>
public class SuperAdminSeedIdempotenceTests
{
    [Fact]
    public void 同库二次启动不重新打印首启超管密码横幅()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"smart-superadmin-reboot-{Guid.NewGuid():N}.db");
        try
        {
            // 首启:不配置密码(AdminPassword=""),真实走"随机生成 + 打印横幅"的建号路径,
            // 模拟系统真正第一次启动、超管账号被建出来。FreshDatabase=true 确保是从零建表播种,不是模板克隆。
            using (var f1 = new AdminAppFactory
                   {
                       DbPath = dbPath,
                       DeleteDbOnDispose = false,
                       FreshDatabase = true,
                       AdminPassword = "",
                   })
            {
                _ = f1.CreateClient();
            }

            // 同一个库文件二次启动:超管早已建号,幂等检查必须能看见这一行(不受租户隔离过滤器影响),
            // 不该重新生成密码,更不该再打一次"首次启动"横幅——那句话只对真首启成立。
            var log = new CaptureLoggerProvider();
            using var f2 = new AdminAppFactory
            {
                DbPath = dbPath,
                DeleteDbOnDispose = false,
                AdminPassword = "",
                Overrides = s => s.AddSingleton<ILoggerProvider>(log),
            };
            _ = f2.CreateClient();

            Assert.DoesNotContain(log.Entries, e => e.Text.Contains("首次启动", StringComparison.Ordinal));
        }
        finally
        {
            TestDb.Cleanup(dbPath, dbPath);
        }
    }
}
