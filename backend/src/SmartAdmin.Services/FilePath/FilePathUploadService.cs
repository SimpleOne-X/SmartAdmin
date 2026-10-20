using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="IFilePathUploadService"/> 默认实现。上传和删除一律经 <see cref="IFilePathResolver.ResolveAsync"/> 定位文件夹——
/// 它会复检服务器根路径的白名单并确保已连上,<see cref="IFilePathResolver.GetRuleAsync"/> 不做这两件事,不能用在这里。
/// <para>写入一律 <see cref="FileMode.CreateNew"/>,撞名由 <c>FilePathFileExists</c> 带出;同名覆盖先写同文件夹里的临时文件再替换,
/// 写到一半失败时旧文件原样保留。命名、校验、写入拆成 <c>protected virtual</c> 步骤,消费者可以子类化覆写其中一步。</para>
/// <para>分片上传把每个会话放在 <c>StagingPath/{uploadId}/</c>:<c>session.json</c> 记用户、编码和文件的声明值,<c>{index}.part</c> 是分片,
/// 合并成 <c>merged</c> 并核对哈希后,和单文件上传走同一个 <see cref="StoreAsync"/> 落到文件夹。
/// 会话目录里只有这些,不放服务器或目标信息(<see cref="FilePathTarget"/> 带着账号密码信封)。
/// 合并先在会话目录里独占创建 <c>merging</c> 标记来认领会话,之后到达的分片和合并都按无效会话处理。</para>
/// </summary>
/// <param name="resolver">定位文件夹。</param>
/// <param name="time">命名用的时钟。</param>
/// <param name="logger">日志。</param>
/// <param name="options">分片上传的暂存目录(<see cref="AdminFilePathOptions.StagingPath"/>)来自它。</param>
/// <param name="currentUser">分片会话绑定的用户;未登录(<see cref="ICurrentUser.UserId"/> 为空)绑定 0。</param>
public class FilePathUploadService(
    IFilePathResolver resolver,
    TimeProvider time,
    ILogger<FilePathUploadService> logger,
    AdminFilePathOptions options,
    ICurrentUser currentUser) : IFilePathUploadService
{
    private const int BufferSize = 81920;

    /// <summary>分片大小:由服务端定,客户端按它切。</summary>
    public const int ChunkSize = 5 * 1024 * 1024;

    private const string SessionFile = "session.json";
    private const string ClaimedFile = "merging";
    private const string MergedFile = "merged";

    /// <summary>NAS 上单个路径段的字节上限(UTF-8)。</summary>
    private const int MaxSegmentBytes = 255;

    /// <summary>覆盖时替换目标文件的最多尝试次数(含第一次)。</summary>
    private const int MaxReplaceAttempts = 12;

    // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION 包成的 HRESULT
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    /// <summary>会话目录里 <c>session.json</c> 的内容。字段就是这些,不加服务器或目标信息。</summary>
    private sealed record ChunkSession(
        long UserId, string Code, string FileName, long Size, string Sha256, string? SubPath, int ChunkSize, int ChunkCount);

    /// <inheritdoc />
    public virtual async Task<FilePathUploadOutput> UploadAsync(string code, FilePathUploadInput input, CancellationToken ct = default)
    {
        var target = await resolver.ResolveAsync(code, ct);
        ValidateUpload(target.Rule, input.FileName, input.Size);

        return await StoreAsync(target, input.FileName, input.Size, input.SubPath, input.Content, ct);
    }

    /// <summary>
    /// 按命名规则定名字并写进文件夹,单文件上传和分片合并共用。年/月/原名_短ID、日期/GUID 的名字是随机生成的,
    /// 撞名就换一个再试一次(<paramref name="content"/> 在撞名时还没被读过);业务指定的路径撞名是调用方要知道的结果,照原样抛出。
    /// 出参的 <see cref="FilePathUploadOutput.Size"/> 取 <paramref name="size"/>。校验由调用方先做。
    /// </summary>
    protected virtual async Task<FilePathUploadOutput> StoreAsync(
        FilePathTarget target, string fileName, long size, string? subPath, Stream content, CancellationToken ct)
    {
        var relative = BuildRelativePath(target.Rule, fileName, subPath);
        try
        {
            await WriteAsync(target, relative, content, ct);
        }
        catch (AdminException e) when (e.Code == ErrorCode.FilePathFileExists && target.Rule.NamingRule != FilePathNamingRule.BusinessPath)
        {
            relative = BuildRelativePath(target.Rule, fileName, subPath);
            await WriteAsync(target, relative, content, ct);
        }

        return new FilePathUploadOutput(relative, FilePathRules.SanitizeFileName(fileName), size);
    }

    /// <inheritdoc />
    public virtual async Task<FilePathChunkInitOutput> ChunkInitAsync(string code, FilePathChunkInitInput input, CancellationToken ct = default)
    {
        var target = await resolver.ResolveAsync(code, ct);
        ValidateUpload(target.Rule, input.FileName, input.Size);
        var sha256 = NormalizeSha256(input.Sha256);
        AdminException.ThrowIf(input.Size < 0 || sha256 is null, ErrorCode.FilePathUploadSessionInvalid);
        // 业务路径的 subPath 不合法在这里就拒绝,不用等整个文件传完;算出来的名字这里用不上
        _ = BuildRelativePath(target.Rule, input.FileName, input.SubPath);

        var chunkCount = (int)((input.Size + ChunkSize - 1) / ChunkSize);
        var uploadId = NewUploadId();
        var dir = Path.Combine(StagingRoot, uploadId);
        var session = new ChunkSession(CurrentUserId, code, input.FileName, input.Size, sha256!, input.SubPath, ChunkSize, chunkCount);
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, SessionFile), JsonSerializer.SerializeToUtf8Bytes(session), ct);
        }
        catch
        {
            DeleteSessionDir(dir);
            throw;
        }

        return new FilePathChunkInitOutput(uploadId, ChunkSize, chunkCount);
    }

    /// <inheritdoc />
    public virtual async Task ChunkAsync(string code, string uploadId, int index, Stream content, CancellationToken ct = default)
    {
        await resolver.ResolveAsync(code, ct);
        var (dir, session) = await LoadSessionAsync(code, uploadId, ct);
        AdminException.ThrowIf(index < 0 || index >= session.ChunkCount, ErrorCode.FilePathUploadSessionInvalid);

        // 非末片正好是 ChunkSize,末片是剩下的
        var expected = index < session.ChunkCount - 1 ? session.ChunkSize : session.Size - (long)(session.ChunkCount - 1) * session.ChunkSize;
        var part = Path.Combine(dir, $"{index}.part");
        // 先写临时名再 Move 覆盖:重传失败时上一次的分片原样保留,长度不对的分片也不会以 .part 的名字留下
        var temp = Path.Combine(dir, $"{index}.{NewShortId()}.tmp");
        var moved = false;
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                long total = 0;
                int read;
                // 边读边数:超出应有长度就立刻停下,多出来的字节不写进暂存,也不再把流读下去
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > expected) throw InvalidSession();
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                if (total != expected) throw InvalidSession();
            }

            // 重传时目标分片可能正被读(合并、杀毒),Windows 上的瞬时占用和覆盖单文件走同一套有限次退避重试
            await ReplaceAsync(temp, part, ct);
            moved = true;
        }
        finally
        {
            if (!moved) TryDelete(temp);
        }
    }

    /// <inheritdoc />
    public virtual async Task<FilePathUploadOutput> ChunkCompleteAsync(string code, string uploadId, CancellationToken ct = default)
    {
        var target = await resolver.ResolveAsync(code, ct);
        var (dir, session) = await LoadSessionAsync(code, uploadId, ct);

        // 缺片不毁掉会话:客户端按 index 补传空缺后可以再合并
        for (var i = 0; i < session.ChunkCount; i++)
            AdminException.ThrowIf(!File.Exists(Path.Combine(dir, $"{i}.part")), ErrorCode.FilePathChunkMissing,
                new Dictionary<string, object?> { ["index"] = i });

        // 认领会话:独占创建标记文件是原子的,同一会话同时只有一个合并能成功。
        // 输的一方(重复点击、客户端超时后重试)得到「会话无效」,不会走到下面的 finally 去删赢家正在合并的目录;
        // 标记在,合并期间到达的分片和合并也都是无效会话,所以 merged 从合并到写入文件夹之间没人动得了。
        // 不用 File.Move 改名认领:Windows 上两个并发的改名可以同时成功;也不靠文件锁:
        // 关掉文件锁(DOTNET_SYSTEM_IO_DISABLEFILELOCKING)或暂存卷由多个副本共用时,锁都不可靠
        ClaimSession(dir);

        try
        {
            var merged = Path.Combine(dir, MergedFile);
            var (sha256, length) = await MergeAsync(dir, session.ChunkCount, merged, ct);
            // 规则可能在 init 之后变过(只读、后缀、上限),按当前规则和实际大小再拦一次;
            // 实际大小与声明不同时哈希必然对不上,不单独判断
            ValidateUpload(target.Rule, session.FileName, length);
            AdminException.ThrowIf(sha256 != session.Sha256, ErrorCode.FilePathChunkHashMismatch);

            await using var content = new FileStream(merged, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            return await StoreAsync(target, session.FileName, length, session.SubPath, content, ct);
        }
        finally
        {
            DeleteSessionDir(dir);
        }
    }

    /// <summary>暂存根目录。<see cref="AdminFilePathOptions.StagingPath"/> 已在启动时规范成完全限定路径。</summary>
    protected virtual string StagingRoot => options.StagingPath;

    /// <summary>分片会话的 Id:32 位小写十六进制,会被拼进暂存路径,所以格式不能变(见 <see cref="FilePathRules.IsUploadId"/>)。</summary>
    protected virtual string NewUploadId() => Guid.NewGuid().ToString("N");

    /// <summary>会话绑定的用户 Id;未登录是 0(HTTP 入口先过角色判断,未登录到不了这里)。</summary>
    private long CurrentUserId => currentUser.UserId ?? 0;

    private static AdminException InvalidSession() => new(ErrorCode.FilePathUploadSessionInvalid);

    /// <summary>
    /// 独占创建 <c>merging</c> 标记,创建成功的那一个调用拥有这次合并。标记已被别人建了(或目录已被合并方删掉)抛 <see cref="ErrorCode.FilePathUploadSessionInvalid"/>;
    /// 其余 IO 失败(磁盘满等)原样抛出,标记没建成,会话保留可重试。
    /// </summary>
    private static void ClaimSession(string dir)
    {
        var claim = Path.Combine(dir, ClaimedFile);
        try
        {
            File.Open(claim, FileMode.CreateNew, FileAccess.Write, FileShare.None).Dispose();
        }
        catch (Exception e) when (e is DirectoryNotFoundException or UnauthorizedAccessException || (e is IOException && File.Exists(claim)))
        {
            throw InvalidSession();
        }
    }

    /// <summary>SHA-256 声明值:去首尾空白、转小写,必须是 64 位十六进制,否则 null。</summary>
    private static string? NormalizeSha256(string? value)
    {
        var sha = value?.Trim().ToLowerInvariant();
        return sha is { Length: 64 } && sha.All(char.IsAsciiHexDigitLower) ? sha : null;
    }

    /// <summary>
    /// 读出会话并核对归属,返回会话目录。<paramref name="uploadId"/> 会被拼进路径,所以第一步先校验格式,
    /// 再确认拼出来的目录在暂存根目录之下,最后核对会话里的用户和编码;任何一步不通过都是 <see cref="ErrorCode.FilePathUploadSessionInvalid"/>。
    /// 会话不存在(没发过、已合并、已被清扫)与归属不符对调用方没有区别。
    /// </summary>
    private async Task<(string Dir, ChunkSession Session)> LoadSessionAsync(string code, string uploadId, CancellationToken ct)
    {
        if (!FilePathRules.IsUploadId(uploadId)) throw InvalidSession();
        var dir = Path.Combine(StagingRoot, uploadId);
        if (!FilePathRules.IsUnder(dir, StagingRoot)) throw InvalidSession();

        // 合并已被认领:此后到达的分片和合并都是无效会话
        if (File.Exists(Path.Combine(dir, ClaimedFile))) throw InvalidSession();

        ChunkSession? session;
        try
        {
            session = JsonSerializer.Deserialize<ChunkSession>(await File.ReadAllBytesAsync(Path.Combine(dir, SessionFile), ct));
        }
        // UnauthorizedAccessException:Windows 上 session.json 正处于待删除状态(合并结束删目录时)读它会得到拒绝访问
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or JsonException)
        {
            throw InvalidSession();
        }

        if (session is null || session.UserId != CurrentUserId || session.Code != code) throw InvalidSession();
        return (dir, session);
    }

    /// <summary>按序把分片拼成 <paramref name="merged"/>,同时算出整体的 SHA-256(小写十六进制)和总长度。</summary>
    private static async Task<(string Sha256, long Length)> MergeAsync(string dir, int chunkCount, string merged, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long length = 0;
        await using var output = new FileStream(merged, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        for (var i = 0; i < chunkCount; i++)
        {
            await using var part = new FileStream(Path.Combine(dir, $"{i}.part"), FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            int read;
            while ((read = await part.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                length += read;
            }
        }

        return (Convert.ToHexStringLower(hash.GetHashAndReset()), length);
    }

    /// <summary>删会话目录。删不掉只记警告,留给 <see cref="FilePathStagingSweeper"/>,不能盖掉调用方正在抛的异常。</summary>
    private void DeleteSessionDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // 已经没了
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "删除分片会话 {Session} 失败,留给暂存清扫器", Path.GetFileName(dir));
        }
    }

    /// <inheritdoc />
    public virtual async Task DeleteAsync(string code, string path, CancellationToken ct = default)
    {
        var target = await resolver.ResolveAsync(code, ct);
        AdminException.ThrowIf(target.Rule.ReadOnly, ErrorCode.FilePathReadOnly);

        // 不合法的路径和隐藏段对调用方一律是「文件不存在」,不暴露哪一条规则挡的
        string relative;
        try
        {
            relative = FilePathRules.NormalizeRelative(path, allowEmpty: false);
        }
        catch (AdminException e) when (e.Code == ErrorCode.FilePathInvalidPath)
        {
            throw new AdminException(ErrorCode.FilePathFileNotFound);
        }

        AdminException.ThrowIf(FilePathRules.HasHiddenSegment(relative), ErrorCode.FilePathFileNotFound);

        var full = resolver.Combine(target, relative);
        try
        {
            // 不用 File.Exists:它把共享断开、拒绝访问、超时都报成 false,调用方会当成「已经删了」而丢掉自己的记录,文件却还在
            AdminException.ThrowIf(GetAttributes(full).HasFlag(FileAttributes.Directory), ErrorCode.FilePathFileNotFound);
            File.Delete(full);
        }
        catch (Exception e) when (FileAccessFailure.IsMissingFile(e))
        {
            throw new AdminException(ErrorCode.FilePathFileNotFound);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogError(e, "删除文件路径 {Code} 下的 {Path} 失败(HResult {HResult:X8})", code, relative, e.HResult);
            throw FileAccessFailure.ToError(e);
        }
    }

    /// <summary>
    /// 读文件属性。文件或其上级目录不存在抛 <see cref="FileNotFoundException"/> / <see cref="DirectoryNotFoundException"/>,
    /// 其余失败(拒绝访问、共享断开)抛原异常——删除据此区分「确实没有」和「访问不了」。
    /// </summary>
    protected virtual FileAttributes GetAttributes(string fullPath) => File.GetAttributes(fullPath);

    /// <summary>
    /// 只读、后缀、大小校验。后缀取清洗后文件名的后缀并转小写,和落盘的后缀是同一个;没有后缀的文件(<c>README</c>)不在白名单里,一律拒绝。
    /// 可写的行缺后缀白名单或大小上限(保存入口写不出,手工改库才有)按配置不完整拒绝,不当作不限。
    /// </summary>
    protected virtual void ValidateUpload(FilePathRule rule, string fileName, long size)
    {
        AdminException.ThrowIf(rule.ReadOnly, ErrorCode.FilePathReadOnly);
        AdminException.ThrowIf(rule.AllowedExtensions.Count == 0 || rule.MaxSizeMb is not > 0, ErrorCode.FilePathRuleIncomplete);

        var ext = FilePathRules.GetExtension(fileName);
        AdminException.ThrowIf(!rule.AllowedExtensions.Contains(ext), ErrorCode.FilePathExtNotAllowed,
            new Dictionary<string, object?> { ["ext"] = ext });

        // MaxSizeMb 没有上界,int 乘法会溢出
        AdminException.ThrowIf(size > rule.MaxSizeMb.Value * 1024L * 1024L, ErrorCode.FilePathTooLarge,
            new Dictionary<string, object?> { ["maxSizeMb"] = rule.MaxSizeMb.Value });
    }

    /// <summary>
    /// 按命名规则定相对路径(<c>/</c> 分隔),后缀统一转小写。年月和日期取服务器本地时间。
    /// 业务指定路径的 <paramref name="subPath"/> 不合法抛 <see cref="ErrorCode.FilePathInvalidPath"/>;其余规则忽略它。
    /// </summary>
    protected virtual string BuildRelativePath(FilePathRule rule, string fileName, string? subPath)
    {
        var clean = FilePathRules.SanitizeFileName(fileName);
        var ext = Path.GetExtension(clean).ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(clean);
        var now = time.GetLocalNow();

        return rule.NamingRule switch
        {
            FilePathNamingRule.YearMonthOriginal =>
                $"{Format(now, "yyyy")}/{Format(now, "MM")}/{stem}_{NewShortId()}{ext}",
            FilePathNamingRule.DateGuid =>
                $"{Format(now, "yyyyMMdd")}/{NewGuid():N}{ext}",
            _ => string.Join('/', SanitizeSubPath(subPath).Append(stem + ext)),
        };

        // 固定用不变区域:某些区域设置的日历(如泰历)会把 yyyy 格式化成别的年份
        static string Format(DateTimeOffset t, string format) => t.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 清洗 <paramref name="subPath"/> 的每一段(按文件名规则:冒号等非法字符换成 <c>_</c>)。
    /// 拒绝绝对路径(首段为空)、空段,以及去掉末尾的点和空格后为空的段(<c>.</c>、<c>..</c>、<c>...</c>、空白)——
    /// 它们若交给清洗会变成 <c>file</c> 这样的普通名字,悄悄改写调用方的意图;
    /// 清洗后超过 255 字节的段也拒绝:清洗只截主干,点后面很长的「后缀」不会被截,而这些段不经后缀白名单。
    /// </summary>
    protected virtual IEnumerable<string> SanitizeSubPath(string? subPath)
    {
        if (string.IsNullOrEmpty(subPath)) return [];

        var segments = new List<string>();
        foreach (var raw in subPath.Replace('\\', '/').Split('/'))
        {
            if (raw.TrimEnd('.', ' ').Length == 0) throw new AdminException(ErrorCode.FilePathInvalidPath);

            var segment = FilePathRules.SanitizeFileName(raw);
            if (Encoding.UTF8.GetByteCount(segment) > MaxSegmentBytes) throw new AdminException(ErrorCode.FilePathInvalidPath);
            segments.Add(segment);
        }

        return segments;
    }

    /// <summary>8 位小写十六进制的短 ID:年/月/原名的名字后缀,以及覆盖用临时文件名的随机部分。</summary>
    protected virtual string NewShortId() => RandomNumberGenerator.GetHexString(8, lowercase: true);

    /// <summary>日期/GUID 命名用的 GUID(v7:按时间有序,目录里大致按上传顺序排)。</summary>
    protected virtual Guid NewGuid() => Guid.CreateVersion7();

    /// <summary>
    /// 把 <paramref name="content"/> 写成文件夹里的 <paramref name="relativePath"/>。
    /// 业务指定路径且同名策略是覆盖时,每次写入都先写临时文件再替换目标(不论目标此刻是否已存在);
    /// 其余情况直接 <see cref="FileMode.CreateNew"/>,撞名抛 <see cref="ErrorCode.FilePathFileExists"/>。
    /// <see cref="IOException"/> 与 <see cref="UnauthorizedAccessException"/> 先记完整异常,再转成
    /// <see cref="ErrorCode.FilePathFolderUnavailable"/>——包括 <paramref name="content"/> 自己读失败。失败时不留半截文件。
    /// </summary>
    protected virtual async Task WriteAsync(FilePathTarget target, string relativePath, Stream content, CancellationToken ct)
    {
        var full = resolver.Combine(target, relativePath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (target.Rule is { NamingRule: FilePathNamingRule.BusinessPath, OnConflict: FilePathConflictMode.Overwrite })
                await WriteReplacingAsync(full, content, ct);
            else
                await WriteNewAsync(full, content, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 不记 target:它带着服务器的账号和密码信封
            logger.LogError(e, "写入文件路径 {Code} 下的 {Path} 失败(HResult {HResult:X8})", target.Rule.Code, relativePath, e.HResult);
            throw FileAccessFailure.ToError(e);
        }
    }

    private async Task WriteNewAsync(string full, Stream content, CancellationToken ct)
    {
        FileStream file;
        try
        {
            file = OpenNew(full);
        }
        catch (IOException) when (File.Exists(full))
        {
            throw new AdminException(ErrorCode.FilePathFileExists);
        }

        var complete = false;
        try
        {
            await CopyAndCloseAsync(file, content, ct);
            complete = true;
        }
        finally
        {
            if (!complete) TryDelete(full);
        }
    }

    /// <summary>
    /// 先写同文件夹里的 <c>.{名}.uploading-{随机}</c>,写完再 <see cref="File.Move(string, string, bool)"/> 覆盖目标。
    /// 不能先截断旧文件再写;临时文件在任何结局下都会删掉(成功时已被移走,删除是空操作)。
    /// </summary>
    private async Task WriteReplacingAsync(string full, Stream content, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.uploading-{NewShortId()}");
        // 创建放在 try 外:临时名撞上别的上传时,finally 不能把别人的临时文件删了
        var file = OpenNew(temp);
        try
        {
            await CopyAndCloseAsync(file, content, ct);
            await ReplaceAsync(temp, full, ct);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// 用临时文件替换目标。Windows 上同一目标同时被几个替换抢着改(或被杀毒软件、仍开着的读取短暂占住)时,
    /// <see cref="File.Move(string, string, bool)"/> 会瞬时失败(拒绝访问、共享冲突、锁冲突),稍等即可;所以只对这几种失败带抖动退避重试,
    /// 最多 <see cref="MaxReplaceAttempts"/> 次,最坏累计等待约 1.4 秒,仍失败就把最后一次的异常抛出去。
    /// </summary>
    private async Task ReplaceAsync(string temp, string full, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, full, overwrite: true);
                return;
            }
            catch (Exception e) when (attempt < MaxReplaceAttempts && IsTransientReplaceFailure(e))
            {
                logger.LogDebug(e, "替换 {Path} 被占用,第 {Attempt} 次尝试失败,稍后重试", full, attempt);
                await Task.Delay(Random.Shared.Next(5, 20 * attempt + 5), ct);
            }
        }
    }

    /// <summary>瞬时占用:拒绝访问(替换抢同一目标时 Windows 这样报),或 Win32 的共享冲突、锁冲突。其余 IO 错误是永久的,不重试。</summary>
    private static bool IsTransientReplaceFailure(Exception e) =>
        e is UnauthorizedAccessException || e is IOException { HResult: SharingViolation or LockViolation };

    private static FileStream OpenNew(string path) =>
        new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

    private static async Task CopyAndCloseAsync(FileStream file, Stream content, CancellationToken ct)
    {
        await using (file) await content.CopyToAsync(file, ct);
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "清理未写完的文件 {Path} 失败", path);
        }
    }
}
