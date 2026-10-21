using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// 文件服务器出参。<b>不含</b>密码和信封——只有 <see cref="HasPassword"/> 标记是否已设置,
/// 密码明文只在保存那一次经过内存(见 <see cref="FileServerService"/>)。
/// </summary>
public sealed record FileServerView(
    long Id, string Name, string RootPath, string? UserName, bool HasPassword, bool Enabled, string? Remark);

/// <summary>
/// 文件服务器新增/编辑入参。<see cref="Password"/> 为 <c>null</c>/空串表示不改动原有密码(新增时即不设密码);
/// <see cref="UserName"/> 为空时账号和密码一并清空。
/// </summary>
public sealed record FileServerSaveInput(
    string Name, string RootPath, string? UserName, string? Password, bool Enabled, string? Remark);

/// <summary>
/// 文件服务器测试连接入参:弹窗里当前填的内容,不要求先保存。<see cref="Id"/> 有值(编辑已有的行)且 <see cref="Password"/> 为空时,
/// 用库里已保存的密码。含明文密码,不要写日志。
/// </summary>
public sealed record FileServerTestInput(long? Id, string RootPath, string? UserName, string? Password);

/// <summary>文件服务器下拉选项(配置文件路径时选服务器用)。</summary>
public sealed record FileServerOption(long Id, string Name, string RootPath, bool Enabled);

/// <summary>文件服务器分页查询入参。</summary>
public sealed record FileServerPageInput : PageInputBase
{
    /// <summary>关键字(模糊匹配名称或根路径,可选)</summary>
    public string? Keyword { get; init; }

    /// <summary>启用状态精确过滤(可选)</summary>
    public bool? Enabled { get; init; }
}
