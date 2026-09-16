using System.Net.Http.Headers;
using SmartAdmin.Core;

namespace SmartAdmin.Tests;

public class TenantControllerTests
{
    private static async Task<HttpClient> SuperAdminClient(AdminAppFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await c.LoginToken("superAdmin", "Test@123456"));
        return c;
    }

    [Fact]
    public async Task Seeded_super_admin_is_platform_admin_and_can_manage_tenants()
    {
        // 种子超管(SuperAdminSeed)天然是初始平台管理员(Task 6),不需要额外授权就能建租户
        using var f = new AdminAppFactory();
        var c = await SuperAdminClient(f);

        var add = await c.PostJson("/api/v1/sys/tenant/add", new
        {
            code = "http-crud", name = "HTTP CRUD Co", isolationMode = 1, enabled = true,
            adminAccount = "http_crud_admin", adminPassword = "Test@123456",
        });
        var addEnv = await add.ReadEnvelope();
        Assert.Equal(0, addEnv.GetProperty("code").GetInt32());
        var newId = addEnv.GetProperty("data").GetInt64();

        var get = await (await c.GetAsync($"/api/v1/sys/tenant/{newId}")).ReadEnvelope();
        Assert.Equal("http-crud", get.GetProperty("data").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Add_with_standalone_mode_returns_TenantIsolationModeNotSupported()
    {
        using var f = new AdminAppFactory();
        var c = await SuperAdminClient(f);

        var add = await (await c.PostJson("/api/v1/sys/tenant/add", new
        {
            code = "standalone-http", name = "X", isolationMode = 2, enabled = true,
            adminAccount = "sa_admin", adminPassword = "Test@123456",
        })).ReadEnvelope();
        Assert.Equal((int)ErrorCode.TenantIsolationModeNotSupported, add.GetProperty("code").GetInt32());
    }
}
