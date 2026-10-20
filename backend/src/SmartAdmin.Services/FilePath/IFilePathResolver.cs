using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// 文件路径解析器:业务编码 → 启用的配置行 + 启用的文件服务器 → 文件夹的完整路径。上传、下载服务都经它定位文件。
/// 两张表走整表读穿透缓存(<see cref="CacheKeys.FilePaths"/>、<see cref="CacheKeys.FileServers"/>),配置变更即时生效。
/// </summary>
public interface IFilePathResolver
{
    /// <summary>
    /// 取编码对应的规则,只读缓存、不做 IO、不连文件服务器。
    /// 编码不存在或已停用抛 <see cref="ErrorCode.FilePathNotFound"/>,所属文件服务器不存在或已停用抛 <see cref="ErrorCode.FileServerNotFound"/>。
    /// 库里的后缀或角色串被改坏(保存入口写不出这种值)时这一行不可用,不会退化成「不限角色」:
    /// 角色串抛 <see cref="ErrorCode.FilePathRuleIncomplete"/>,后缀抛 <see cref="ErrorCode.FilePathExtensionsInvalid"/>。
    /// </summary>
    Task<FilePathRule> GetRuleAsync(string code, CancellationToken ct = default);

    /// <summary>
    /// 在 <see cref="GetRuleAsync"/> 的基础上:对服务器根路径做白名单复检(白名单可能在保存之后改小,
    /// 不通过抛 <see cref="ErrorCode.FileServerRootNotAllowed"/>),调连接器确保已连上,再拼出文件夹完整路径。
    /// 连接器的失败转成 <see cref="ErrorCode.FilePathFolderUnavailable"/>(args.reason 是原因码)。
    /// 连接之前先过 <see cref="IFilePathCircuit.ThrowIfOpen"/>:这台服务器的熔断打开时直接抛同一个错误码,不去连。
    /// </summary>
    Task<FilePathTarget> ResolveAsync(string code, CancellationToken ct = default);

    /// <summary>
    /// 把业务传入的相对路径(可含 <c>\</c>)规范化后拼到文件夹下,返回完全限定路径;
    /// 路径不合法或拼出来跳出文件夹抛 <see cref="ErrorCode.FilePathInvalidPath"/>。
    /// </summary>
    string Combine(FilePathTarget target, string relativePath);
}
