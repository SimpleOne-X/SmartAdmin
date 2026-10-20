using SqlSugar;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// 文件服务器——一台 NAS 的一个共享(或一个本机目录)登记一行:根路径 + 访问账号。
/// 「文件路径」(<see cref="SysFilePath"/>)在它的根路径之下再选子文件夹落文件。
/// </summary>
[SugarTable("sys_file_server", TableDescription = "文件服务器")]
[SugarIndex("idx_sys_file_server_name", nameof(Name), OrderByType.Asc, IsUnique = true)]
public class SysFileServer : BaseEntity
{
    /// <summary>名称(唯一)。列长比输入上限(64)大,给软删时追加的 <c>_del_{id}</c> 留位置</summary>
    [SugarColumn(Length = 100, ColumnDescription = "名称(唯一)")]
    public string Name { get; set; } = "";

    /// <summary>根路径,绝对路径,必须落在 <c>SmartAdmin:FilePath:AllowedRoots</c> 白名单内</summary>
    [SugarColumn(Length = 260, ColumnDescription = "根路径(绝对路径,须在白名单内)")]
    public string RootPath { get; set; } = "";

    /// <summary>访问账号(<c>域\账号</c> 或 <c>账号</c>);null = 用后端自己的运行身份访问</summary>
    [SugarColumn(Length = 128, IsNullable = true, ColumnDescription = "访问账号")]
    public string? UserName { get; set; }

    /// <summary>访问密码的 ISecretProtector 加密信封;null = 未设置</summary>
    [SugarColumn(Length = 2048, IsNullable = true, ColumnDescription = "访问密码加密信封(ISecretProtector)")]
    public string? PasswordProtected { get; set; }

    /// <summary>是否启用;停用后挂在它下面的文件路径全部不能上传、下载</summary>
    [SugarColumn(ColumnDescription = "是否启用")]
    public bool Enabled { get; set; }

    [SugarColumn(Length = 512, IsNullable = true, ColumnDescription = "备注")]
    public string? Remark { get; set; }
}
