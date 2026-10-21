using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SmartAdmin.Services;

/// <summary>
/// 访问某台文件服务器所需的信息。<see cref="PasswordProtected"/> 是 <c>ISecretProtector</c> 的信封,明文由连接器自己解开、不外传。
/// </summary>
public sealed record FileServerTarget(long Id, string RootPath, string? UserName, string? PasswordProtected);

/// <summary>
/// 文件服务器连接器(扩展点):在文件操作之前,为需要账号的服务器建立访问会话。
/// 没填账号的服务器用后端自己的运行身份访问,不需要连接。
/// </summary>
public interface IFileServerConnector
{
    /// <summary>当前平台能否用账号密码连接共享。不能时,保存文件服务器不强制「填了账号必须是共享路径」,账号被忽略。</summary>
    bool SupportsCredentials { get; }

    /// <summary>
    /// 确保已按 <paramref name="target"/> 的凭据连上。连不上抛 <see cref="FileServerConnectException"/>。
    /// <paramref name="retryFailed"/> 为 true 时(用户点了测试连接)忽略此前记下的失败,重新试一次。
    /// </summary>
    Task EnsureConnectedAsync(FileServerTarget target, bool retryFailed, CancellationToken ct = default);
}

/// <summary>
/// 不支持账号连接的平台上的默认连接器:什么都不做。
/// 填了账号的服务器每个 Id 只记一次警告,免得每次上传下载都刷日志。
/// </summary>
public class NoopFileServerConnector(ILogger<NoopFileServerConnector> logger) : IFileServerConnector
{
    private readonly ConcurrentDictionary<long, byte> _warned = new();

    /// <inheritdoc />
    public virtual bool SupportsCredentials => false;

    /// <inheritdoc />
    public virtual Task EnsureConnectedAsync(FileServerTarget target, bool retryFailed, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(target.UserName) && _warned.TryAdd(target.Id, 0))
            logger.LogWarning("文件服务器 {ServerId} 填了访问账号,当前平台不支持用账号连接,已忽略", target.Id);
        return Task.CompletedTask;
    }
}
