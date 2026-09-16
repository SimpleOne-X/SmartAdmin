using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 批量删除约定的回归:普通用户整批软删生效;集合里混入超管则整体拒绝(SuperAdminProtected)、且一个都不删——
/// 守住"超管不可删"不变量不被批量入口绕过,以及批量删除的原子性(全成或全不动)。
/// </summary>
public class BatchDeleteTests
{
    private static async Task<long> AddUser(IServiceProvider sp, string suffix)
    {
        var users = sp.GetRequiredService<IUserService>();
        var added = await users.AddAsync(new AddUserInput
        {
            Account = "batch-" + suffix,
            Password = "Batch@123456",
            Name = "批量用户" + suffix,
            Enabled = true,
        });
        return added.Id;
    }

    [Fact]
    public async Task Batch_delete_soft_deletes_all_normal_users()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var users = sp.GetRequiredService<IUserService>();
        var repo = sp.GetRequiredService<IRepository<SysUser>>();

        var id1 = await AddUser(sp, "a");
        var id2 = await AddUser(sp, "b");

        await users.DeleteBatchAsync([id1, id2]);

        // 全局软删过滤器让已删行对查询不可见 → 两个都查不到
        Assert.False(await repo.AnyAsync(u => u.Id == id1));
        Assert.False(await repo.AnyAsync(u => u.Id == id2));
    }

    [Fact]
    public async Task Batch_delete_rejects_whole_set_when_super_admin_included()
    {
        using var f = new AdminAppFactory();

        // 经 HTTP 以已登录 superAdmin 身份操作(而非后台 DI 作用域直调服务):DeleteBatchAsync 内部按
        // currentUser.TenantId 查这批 Id 判断"是否含超管",后台作用域没有租户上下文会让 superId 在那次
        // 查询里"隐形"(过滤器退化成只找得到租户为空的行),超管保护形同虚设——只有走真实已认证请求
        // 才是这条保护路径的真实调用场景。
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await c.LoginToken("superAdmin", "Test@123456"));

        var superId = (await (await c.GetAsync("/api/v1/personal/profile")).ReadEnvelope())
            .GetProperty("data").GetProperty("id").GetInt64();
        var normalId = (await (await c.PostJson("/api/v1/sys/user", new
        {
            account = "batch-c", password = "Batch@123456", name = "批量用户c", enabled = true, roleIds = Array.Empty<long>(),
        })).ReadEnvelope()).GetProperty("data").GetProperty("id").GetInt64();

        // 集合含超管 → 抛 SuperAdminProtected
        var del = await (await c.PostJson("/api/v1/sys/user/batch-delete",
            new { ids = new[] { normalId, superId } })).ReadEnvelope();
        Assert.Equal((int)ErrorCode.SuperAdminProtected, del.GetProperty("code").GetInt32());

        // 原子性:整批回滚,普通用户也没被删
        using var scope = f.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<SysUser>>();
        Assert.True(await repo.AsQueryable().ClearFilter<ITenantScoped>().AnyAsync(u => u.Id == normalId));
        Assert.True(await repo.AsQueryable().ClearFilter<ITenantScoped>().AnyAsync(u => u.Id == superId));
    }
}
