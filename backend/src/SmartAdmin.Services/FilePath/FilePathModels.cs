using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>文件路径出参。角色 Id 已拆成升序去重的列表,空列表 = 所有登录用户。</summary>
public sealed record FilePathView(
    long Id, string Code, string Name, long ServerId, string? ServerName, string? Folder,
    FilePathNamingRule NamingRule, FilePathConflictMode OnConflict, bool ReadOnly, string? AllowedExtensions, int? MaxSizeMb,
    IReadOnlyList<long> UploadRoleIds, IReadOnlyList<long> DownloadRoleIds, bool Enabled, string? Remark);

/// <summary>文件路径新增入参。保存时的规范化与取舍规则见 <see cref="FilePathConfigService"/>。</summary>
public sealed record FilePathAddInput(
    string Code, string Name, long ServerId, string? Folder, FilePathNamingRule NamingRule,
    FilePathConflictMode OnConflict, bool ReadOnly, string? AllowedExtensions, int? MaxSizeMb,
    IReadOnlyList<long>? UploadRoleIds, IReadOnlyList<long>? DownloadRoleIds, bool Enabled, string? Remark);

/// <summary>文件路径编辑入参:与新增相同,只是没有 <c>Code</c>——编码是业务引用它的键,保存后不能改。</summary>
public sealed record FilePathUpdateInput(
    string Name, long ServerId, string? Folder, FilePathNamingRule NamingRule,
    FilePathConflictMode OnConflict, bool ReadOnly, string? AllowedExtensions, int? MaxSizeMb,
    IReadOnlyList<long>? UploadRoleIds, IReadOnlyList<long>? DownloadRoleIds, bool Enabled, string? Remark);

/// <summary>文件路径分页查询入参。</summary>
public sealed record FilePathPageInput : PageInputBase
{
    /// <summary>关键字(模糊匹配编码或名称,可选)</summary>
    public string? Keyword { get; init; }

    /// <summary>文件服务器精确过滤(可选)</summary>
    public long? ServerId { get; init; }

    /// <summary>启用状态精确过滤(可选)</summary>
    public bool? Enabled { get; init; }
}

/// <summary>文件路径测试连接入参:弹窗里当前填的服务器、子文件夹和读写方式,不要求先保存。</summary>
public sealed record FilePathTestInput(long ServerId, string? Folder, bool ReadOnly);

/// <summary>测试连接的结果。<see cref="Reason"/> 是 <see cref="FileAccessFailure"/> 的原因码(前端语言包 <c>error.reason.*</c>),成功时为 null。</summary>
public sealed record FileAccessTestResult(bool Ok, string? Reason, int LatencyMs);

/// <summary>一行文件服务器或文件路径的在线状态。<see cref="Reason"/> 同 <see cref="FileAccessTestResult.Reason"/>。</summary>
public sealed record FileAccessStatus(long Id, bool Online, string? Reason);

/// <summary>配置页拿来给提示和候选的环境信息:白名单根目录,以及当前平台能不能用账号连接共享。</summary>
public sealed record FilePathEnvironment(IReadOnlyList<string> AllowedRoots, bool SupportsCredentials);

/// <summary>
/// 解析后的文件路径规则——上传、下载服务需要的全部配置,后缀与角色已拆成列表。
/// <see cref="Folder"/> 是相对服务器根路径的子文件夹(null = 根路径本身)。
/// </summary>
public sealed record FilePathRule(
    long Id, string Code, FileServerTarget Server, string? Folder, FilePathNamingRule NamingRule,
    FilePathConflictMode OnConflict, bool ReadOnly, IReadOnlyList<string> AllowedExtensions, int? MaxSizeMb,
    IReadOnlyList<long> UploadRoleIds, IReadOnlyList<long> DownloadRoleIds);

/// <summary>已通过白名单复检、连接器已就绪的落盘位置;<see cref="FolderFullPath"/> 是文件夹的完全限定路径。</summary>
public sealed record FilePathTarget(FilePathRule Rule, string FolderFullPath);
