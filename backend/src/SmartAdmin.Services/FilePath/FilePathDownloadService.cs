using Microsoft.Extensions.Logging;
using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="IFilePathDownloadService"/> 默认实现。下载一律经 <see cref="IFilePathResolver.ResolveAsync"/> 定位文件夹——
/// 它会复检服务器根路径的白名单并确保已连上,<see cref="IFilePathResolver.GetRuleAsync"/> 不做这两件事,不能用在这里。
/// <para>路径处理的顺序和删除一致:先规范化(不合法抛 <c>FilePathInvalidPath</c>),再看隐藏段(一律「文件不存在」),最后拼到文件夹下。
/// 读取带 <see cref="FileShare.Delete"/>:Windows 上覆盖上传用 <see cref="File.Replace(string, string, string?, bool)"/> 替换目标,
/// 读者不带它就替换不了读着的文件。
/// 读属性、打开拆成 <c>protected virtual</c> 步骤,消费者可以子类化覆写其中一步。
/// 读属性和打开在 <see cref="IFilePathCircuit.RunAsync{T}"/> 里做:受在途上限约束,网络类失败打开这台服务器的熔断。</para>
/// </summary>
/// <param name="resolver">定位文件夹。</param>
/// <param name="logger">日志。</param>
/// <param name="circuit">文件服务器熔断与在途上限。</param>
public class FilePathDownloadService(IFilePathResolver resolver, ILogger<FilePathDownloadService> logger, IFilePathCircuit circuit)
    : IFilePathDownloadService
{
    private const int BufferSize = 81920;

    /// <inheritdoc />
    public virtual async Task<FilePathDownload> OpenReadAsync(string code, string path, CancellationToken ct = default)
    {
        var (target, full, relative) = await LocateAsync(code, path, ct);
        return await circuit.RunAsync(target.Rule.Server.Id, () => Task.FromResult(Open(code, full, relative)));
    }

    /// <inheritdoc />
    public virtual async Task<string> GetFullPathAsync(string code, string path, CancellationToken ct = default) =>
        (await LocateAsync(code, path, ct)).Full;

    private FilePathDownload Open(string code, string full, string relative)
    {
        FileStream? stream = null;
        var handedOver = false;
        try
        {
            // 不用 File.Exists:它把共享断开、拒绝访问、超时都报成 false,调用方会当成「文件已经没了」
            AdminException.ThrowIf(GetAttributes(full).HasFlag(FileAttributes.Directory), ErrorCode.FilePathFileNotFound);
            stream = OpenRead(full);
            var download = new FilePathDownload(stream, Path.GetFileName(full), Path.GetExtension(full).ToLowerInvariant(), stream.Length);
            handedOver = true;
            return download;
        }
        catch (Exception e) when (FileAccessFailure.IsMissingFile(e))
        {
            throw new AdminException(ErrorCode.FilePathFileNotFound);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 不记 target:它带着服务器的账号和密码信封
            logger.LogError(e, "读取文件路径 {Code} 下的 {Path} 失败(HResult {HResult:X8})", code, relative, e.HResult);
            throw FileAccessFailure.ToError(e);
        }
        finally
        {
            if (!handedOver) stream?.Dispose();
        }
    }

    /// <summary>
    /// 定位:解析文件夹,把业务传入的路径规范化、挡掉隐藏段、拼成完全限定路径。返回的相对路径只用来写日志。
    /// 不合法的路径抛 <see cref="ErrorCode.FilePathInvalidPath"/>,隐藏段抛 <see cref="ErrorCode.FilePathFileNotFound"/>——
    /// 规范化先于隐藏段判断,所以 <c>../.x</c> 报的是前者。
    /// </summary>
    protected virtual async Task<(FilePathTarget Target, string Full, string Relative)> LocateAsync(string code, string path, CancellationToken ct)
    {
        var target = await resolver.ResolveAsync(code, ct);
        var relative = FilePathRules.NormalizeRelative(path, allowEmpty: false);
        AdminException.ThrowIf(FilePathRules.HasHiddenSegment(relative), ErrorCode.FilePathFileNotFound);
        return (target, resolver.Combine(target, relative), relative);
    }

    /// <summary>
    /// 读文件属性。文件或其上级目录不存在抛 <see cref="FileNotFoundException"/> / <see cref="DirectoryNotFoundException"/>,
    /// 其余失败(拒绝访问、共享断开)抛原异常——据此区分「确实没有」和「访问不了」。
    /// </summary>
    protected virtual FileAttributes GetAttributes(string fullPath) => File.GetAttributes(fullPath);

    /// <summary>
    /// 以只读、异步方式打开,共享模式带 <see cref="FileShare.Delete"/>:读着的时候同名覆盖上传(先写临时文件再替换目标)仍能成功。
    /// Windows 上替换靠 <see cref="File.Replace(string, string, string?, bool)"/>,它要求读者带这个共享标志;<see cref="File.Move(string, string, bool)"/> 的覆盖不论共享标志都替换不了打开着的文件。
    /// </summary>
    protected virtual FileStream OpenRead(string fullPath) =>
        new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize, useAsync: true);
}
