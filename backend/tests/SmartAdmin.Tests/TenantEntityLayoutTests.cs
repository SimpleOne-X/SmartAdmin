using SmartAdmin.SqlSugar;
using SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>租户实体基类层级的纯类型契约——不接触数据库,只锁字段/接口/默认值。</summary>
public class TenantEntityLayoutTests
{
    [Fact]
    public void TenantEntity_implements_ITenantScoped_with_nullable_default()
    {
        var doc = new ProbeTenantEntity();
        Assert.Null(doc.TenantId);            // 未显式赋值时为空——AOP 填充前的正常状态
        ITenantScoped scoped = doc;
        Assert.Null(scoped.TenantId);
    }

    [Fact]
    public void TenantDataEntity_combines_org_scope_and_tenant_scope()
    {
        var doc = new ProbeTenantDataEntity();
        Assert.IsAssignableFrom<IOrgScoped>(doc);
        Assert.IsAssignableFrom<ITenantScoped>(doc);
    }

    [SugarTable("probe_tenant_entity")]
    private class ProbeTenantEntity : TenantEntity;

    [SugarTable("probe_tenant_data_entity")]
    private class ProbeTenantDataEntity : TenantDataEntity;
}
