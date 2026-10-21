using SqlSugar;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>文件落盘的命名规则(<c>sys_file_path.NamingRule</c>)</summary>
public enum FilePathNamingRule
{
    /// <summary>年/月/原名_短ID:<c>{yyyy}/{MM}/{清洗后的原名}_{8 位随机十六进制}{后缀}</c>,适合附件</summary>
    YearMonthOriginal = 0,

    /// <summary>业务指定路径:<c>{subPath}/{清洗后的文件名}</c>,适合路径本身有业务含义的文件</summary>
    BusinessPath = 1,

    /// <summary>日期/GUID:<c>{yyyyMMdd}/{GUID}{后缀}</c>,不保留原文件名</summary>
    DateGuid = 2,
}

/// <summary>业务指定路径撞名时的处理方式(<c>sys_file_path.OnConflict</c>);其余命名规则不会撞名</summary>
public enum FilePathConflictMode
{
    /// <summary>拒绝上传</summary>
    Reject = 0,

    /// <summary>覆盖已有文件</summary>
    Overwrite = 1,
}

/// <summary>
/// 文件路径——业务按编码引用的一个落盘位置:某台文件服务器下的一个子文件夹,加上命名、同名、后缀、大小和谁能上传下载的规则。
/// 与 <see cref="SysFile"/>(经 <c>IFileService</c> 上传、记账的通用附件)互相独立。
/// </summary>
[SugarTable("sys_file_path", TableDescription = "文件路径")]
[SugarIndex("idx_sys_file_path_code", nameof(Code), OrderByType.Asc, IsUnique = true)]
public class SysFilePath : BaseEntity
{
    /// <summary>编码(唯一;业务方用它引用)。列长比输入上限(64)大,给软删时追加的 <c>_del_{id}</c> 留位置</summary>
    [SugarColumn(Length = 100, ColumnDescription = "编码(唯一)")]
    public string Code { get; set; } = "";

    [SugarColumn(Length = 100, ColumnDescription = "名称")]
    public string Name { get; set; } = "";

    /// <summary>所属文件服务器(<see cref="SysFileServer"/> 的 Id)</summary>
    [SugarColumn(ColumnDescription = "文件服务器 Id")]
    public long ServerId { get; set; }

    /// <summary>子文件夹,相对文件服务器根路径,正斜杠分隔;null/空 = 根路径本身</summary>
    [SugarColumn(Length = 260, IsNullable = true, ColumnDescription = "子文件夹(相对根路径)")]
    public string? Folder { get; set; }

    [SugarColumn(ColumnDescription = "命名规则:0=年/月/原名_短ID 1=业务指定路径 2=日期/GUID")]
    public FilePathNamingRule NamingRule { get; set; } = FilePathNamingRule.YearMonthOriginal;

    [SugarColumn(ColumnDescription = "同名处理:0=拒绝 1=覆盖")]
    public FilePathConflictMode OnConflict { get; set; } = FilePathConflictMode.Reject;

    /// <summary>只读:不开放上传,只提供下载</summary>
    [SugarColumn(ColumnDescription = "只读(不允许上传)")]
    public bool ReadOnly { get; set; }

    /// <summary>后缀白名单,小写带点、逗号分隔(如 <c>.pdf,.docx</c>);可写的行必填,只读的行不填</summary>
    [SugarColumn(Length = 500, IsNullable = true, ColumnDescription = "后缀白名单(逗号分隔)")]
    public string? AllowedExtensions { get; set; }

    /// <summary>单文件大小上限(MB);可写的行必填且大于 0,只读的行不填</summary>
    [SugarColumn(IsNullable = true, ColumnDescription = "单文件大小上限(MB)")]
    public int? MaxSizeMb { get; set; }

    /// <summary>可上传的角色 Id,逗号分隔;null/空 = 所有登录用户</summary>
    [SugarColumn(Length = 1000, IsNullable = true, ColumnDescription = "上传角色 Id(逗号分隔)")]
    public string? UploadRoleIds { get; set; }

    /// <summary>可下载的角色 Id,逗号分隔;null/空 = 所有登录用户</summary>
    [SugarColumn(Length = 1000, IsNullable = true, ColumnDescription = "下载角色 Id(逗号分隔)")]
    public string? DownloadRoleIds { get; set; }

    /// <summary>是否启用;停用后上传、下载、签名直链都拒绝</summary>
    [SugarColumn(ColumnDescription = "是否启用")]
    public bool Enabled { get; set; }

    [SugarColumn(Length = 512, IsNullable = true, ColumnDescription = "备注")]
    public string? Remark { get; set; }
}
