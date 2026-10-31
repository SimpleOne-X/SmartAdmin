using SqlSugar;
using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.TestHost;

/// <summary>
/// 示例"可按行共享给所有机构"的业务实体——继承 <see cref="OrgAuditEntity"/>,物理删除、无回收站。
/// 用来验证共享标记 <c>IsShared</c> 与归属机构 <c>CreateOrgId</c> 在种子升级、老库补列时的行为。
/// </summary>
[SugarTable("sample_org_doc", TableDescription = "示例可共享机构实体(集成测试)")]
public class SampleOrgDoc : OrgAuditEntity
{
    [SugarColumn(Length = 128, ColumnDescription = "标题")]
    public string Title { get; set; } = "";
}

/// <summary>
/// 开了升级同步的种子:升级时只该把标题这类业务列刷回种子值,
/// 用户设的共享标记和审计写入的归属机构必须留着。
/// </summary>
public sealed class SampleOrgDocSeed : ISeedData<SampleOrgDoc>
{
    public const long SeedId = SmartSeedIds.ConsumerMin + 20;

    public bool SyncOnUpgrade => true;

    public IEnumerable<SampleOrgDoc> HasData() =>
    [
        new SampleOrgDoc { Id = SeedId, Title = "种子标题" },
    ];
}
