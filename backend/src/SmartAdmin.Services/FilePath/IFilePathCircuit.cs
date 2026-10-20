namespace SmartAdmin.Services;

/// <summary>
/// 文件服务器的熔断与在途上限(扩展点,单例)。服务器不通时,文件系统调用会同步卡到 SMB 客户端自己的超时(常是几十秒),
/// 请求一多就占满线程池,登录等无关接口也跟着变慢。所以按服务器 Id:
/// <list type="bullet">
///   <item>网络类失败(找不到网络路径、超时)后一段时间内,这台服务器的上传、下载、删除直接报
///         <see cref="Core.ErrorCode.FilePathFolderUnavailable"/>(原因沿用那次失败的),不再碰文件系统;过了这段时间放一个请求过去试探。</item>
///   <item>同时在做的文件系统操作有上限,满了的请求直接报同一个错误码,原因是 <see cref="FileAccessFailure.Busy"/>。</item>
/// </list>
/// 解析器在连接之前调 <see cref="ThrowIfOpen"/>(每个请求只过一次熔断),上传、下载、删除和探针把真正碰文件系统的那一段交给 <see cref="RunAsync{T}"/>。
/// </summary>
public interface IFilePathCircuit
{
    /// <summary>
    /// 熔断打开时抛 <see cref="Core.ErrorCode.FilePathFolderUnavailable"/>,原因是打开它的那次失败的原因。
    /// 打开满一段时间后放一个请求通过去试探,它的结果出来之前其余请求照旧被挡;它一直没有结果,下一段时间后再放一个。
    /// </summary>
    void ThrowIfOpen(long serverId);

    /// <summary>
    /// 在这台服务器的在途上限之下执行一段访问文件服务器的操作,不看熔断(请求已经在 <see cref="ThrowIfOpen"/> 过了熔断,探针本来就不该被挡)。
    /// 在途已满抛 <see cref="Core.ErrorCode.FilePathFolderUnavailable"/>(原因 <see cref="FileAccessFailure.Busy"/>),不执行 <paramref name="access"/>。
    /// <paramref name="access"/> 的异常原样抛出:网络类失败打开熔断(<paramref name="probe"/> 为 true 时不打开),其余失败不改变熔断;成功关闭熔断。
    /// </summary>
    /// <param name="serverId">文件服务器 Id;熔断和在途名额都按它分开。</param>
    /// <param name="access">真正碰文件系统的那一段。</param>
    /// <param name="probe">测试连接、在线状态:测的可能是还没保存的配置,失败不拿来挡已保存的配置;成功说明服务器通了。</param>
    Task<T> RunAsync<T>(long serverId, Func<Task<T>> access, bool probe = false);

    /// <summary>记一次在 <see cref="RunAsync{T}"/> 之外发生的失败(如建立连接):网络类失败打开熔断,其余不改变熔断。</summary>
    void ReportFailure(long serverId, Exception failure);
}

/// <summary><see cref="IFilePathCircuit"/> 的便捷重载。</summary>
public static class FilePathCircuitExtensions
{
    /// <summary>没有返回值的 <see cref="IFilePathCircuit.RunAsync{T}"/>。</summary>
    public static Task RunAsync(this IFilePathCircuit circuit, long serverId, Func<Task> access, bool probe = false) =>
        circuit.RunAsync(serverId, async () =>
        {
            await access();
            return true;
        }, probe);
}
