using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>
/// 文件路径分片上传与暂存清扫:init 先于一切拦截、两片的完整流程、缺片与哈希、分片长度与编号、
/// 伪造/串用的 uploadId、规则中途变化,以及清扫器只删过期的会话目录。
/// 服务从宿主解析(<see cref="ICurrentUser"/> 换成可改 UserId 的替身),暂存目录是环境里的临时目录。
/// </summary>
public class FilePathChunkUploadTests
{
    /// <summary>与服务端约定一致的分片大小。</summary>
    private const int ChunkSize = 5 * 1024 * 1024;

    private const int TwoMb = 2 * 1024 * 1024;

    /// <summary>7 MB,正好切成 5 MB + 2 MB 两片。</summary>
    private static readonly byte[] Big = MakeBytes(7 * 1024 * 1024);

    /// <summary>一片就装得下的小文件。</summary>
    private static readonly byte[] Small = MakeBytes(1000);

    private static byte[] MakeBytes(int length)
    {
        var bytes = new byte[length];
        new Random(42).NextBytes(bytes);
        return bytes;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task<AdminException> Rejected(Task task) => await Assert.ThrowsAsync<AdminException>(() => task);

    private static async Task AssertInvalid(Task task) =>
        Assert.Equal(ErrorCode.FilePathUploadSessionInvalid, (await Rejected(task)).Code);

    /// <summary>UserId 可以在用例中途改的当前用户。</summary>
    private sealed class MutableUser(long? userId) : ICurrentUser
    {
        public long? UserId { get; set; } = userId;
        public bool IsAuthenticated => UserId is not null;
        public string? SessionId => null;
        public bool IsSuperAdmin => false;
        public long? OrgId => null;
        public string? IpAddress => null;
        public string? UserAgent => null;
    }

    private static FilePathTestEnv NewEnv(MutableUser user) =>
        FilePathTestEnv.Create(overrides: s => s.Replace(ServiceDescriptor.Singleton<ICurrentUser>(user)));

    private static async Task<(long Id, string Code)> AddPathAsync(
        FilePathTestEnv env, FilePathNamingRule naming = FilePathNamingRule.YearMonthOriginal,
        bool readOnly = false, int? maxSizeMb = 10)
    {
        var code = $"c-{Guid.NewGuid():N}";
        var id = await env.Service<IFilePathConfigService>().AddAsync(new FilePathAddInput(
            code, "订单附件", await env.AddServerAsync(), "Docs", naming, FilePathConflictMode.Reject, readOnly,
            readOnly ? null : ".pdf", readOnly ? null : maxSizeMb, null, null, true, null));
        return (id, code);
    }

    /// <summary>改规则:是否启用、大小上限;其余保持不变。</summary>
    private static async Task ReconfigureAsync(FilePathTestEnv env, long id, bool enabled = true, int? maxSizeMb = null)
    {
        var config = env.Service<IFilePathConfigService>();
        var v = await config.GetAsync(id);
        await config.UpdateAsync(id, new FilePathUpdateInput(
            v.Name, v.ServerId, v.Folder, v.NamingRule, v.OnConflict, v.ReadOnly, v.AllowedExtensions, maxSizeMb ?? v.MaxSizeMb,
            v.UploadRoleIds, v.DownloadRoleIds, enabled, v.Remark));
    }

    private static FilePathChunkInitInput InitInput(
        byte[] content, string fileName = "大文件.pdf", string? sha = null, string? subPath = null, long? size = null) =>
        new(fileName, size ?? content.Length, sha ?? Sha(content), subPath);

    private static byte[] Slice(byte[] content, int index) =>
        content.AsSpan(index * ChunkSize, Math.Min(ChunkSize, content.Length - index * ChunkSize)).ToArray();

    private static Task UploadChunk(IFilePathUploadService service, string code, string uploadId, int index, byte[] content) =>
        service.ChunkAsync(code, uploadId, index, new MemoryStream(Slice(content, index)));

    /// <summary>init → 全部分片 → complete。</summary>
    private static async Task<FilePathUploadOutput> UploadWholeAsync(
        IFilePathUploadService service, string code, byte[] content, string fileName = "大文件.pdf", string? subPath = null)
    {
        var init = await service.ChunkInitAsync(code, InitInput(content, fileName, subPath: subPath));
        for (var i = 0; i < init.ChunkCount; i++) await UploadChunk(service, code, init.UploadId, i, content);
        return await service.ChunkCompleteAsync(code, init.UploadId);
    }

    private static string SessionDir(FilePathTestEnv env, string uploadId) => Path.Combine(env.Staging, uploadId);

    private static string[] FileNames(string dir) =>
        [.. Directory.GetFiles(dir).Select(f => Path.GetFileName(f)).Order()];

    private static string DiskPath(FilePathTestEnv env, string relative) => Path.Combine(env.Root, "Docs", relative);

    /// <summary>不用 <c>Assert.Equal</c> 比字节数组:失败时它会把几百万个元素逐个打出来。</summary>
    private static async Task AssertSameBytes(byte[] expected, string path)
    {
        var actual = await File.ReadAllBytesAsync(path);
        Assert.True(expected.AsSpan().SequenceEqual(actual), $"{path} 的内容与上传的不一致(期望 {expected.Length} 字节,实际 {actual.Length} 字节)");
    }

    /// <summary>读一次就读完的流,统计被读走的字节数;永远读得出数据,用来验证服务端会在超长时自己停下。</summary>
    private sealed class EndlessStream : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            BytesRead += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Clear();
            BytesRead += buffer.Length;
            return ValueTask.FromResult(buffer.Length);
        }
    }

    /// <summary>先吐出 <c>sent</c> 个字节,再抛 <see cref="IOException"/>:模拟传输中途断开。</summary>
    private sealed class DroppedStream(int sent) : Stream
    {
        private int _left = sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_left == 0) throw new IOException("连接中断");
            var n = Math.Min(_left, buffer.Length);
            buffer.Span[..n].Fill((byte)'x');
            _left -= n;
            return ValueTask.FromResult(n);
        }
    }

    [Fact]
    public async Task Init_rejects_before_any_upload()
    {
        await using var env = NewEnv(new MutableUser(1));
        var service = env.Service<IFilePathUploadService>();
        var (_, readOnly) = await AddPathAsync(env, readOnly: true);
        var (_, limited) = await AddPathAsync(env, maxSizeMb: 1);

        Assert.Equal(ErrorCode.FilePathReadOnly, (await Rejected(service.ChunkInitAsync(readOnly, InitInput(Small)))).Code);

        var ext = await Rejected(service.ChunkInitAsync(limited, InitInput(Small, fileName: "a.exe")));
        Assert.Equal(ErrorCode.FilePathExtNotAllowed, ext.Code);
        Assert.Equal(".exe", ext.Args!["ext"]);

        var large = await Rejected(service.ChunkInitAsync(limited, InitInput(Small, size: 1024 * 1024 + 1)));
        Assert.Equal(ErrorCode.FilePathTooLarge, large.Code);
        Assert.Equal(1, large.Args!["maxSizeMb"]);

        // 被拒绝的上传不占暂存空间
        Assert.Empty(Directory.GetFileSystemEntries(env.Staging));
    }

    [Fact]
    public async Task Init_rejects_malformed_input()
    {
        await using var env = NewEnv(new MutableUser(1));
        var service = env.Service<IFilePathUploadService>();
        var (_, code) = await AddPathAsync(env);
        var (_, business) = await AddPathAsync(env, FilePathNamingRule.BusinessPath);

        // SHA-256 必须是 64 位十六进制:长度不对、含非十六进制字符、中间夹空白都在 init 时拒绝
        foreach (var sha in new[] { "", " ", "abc", new string('a', 63), new string('a', 65), new string('g', 64), new string('a', 32) + " " + new string('a', 31) })
            await AssertInvalid(service.ChunkInitAsync(code, InitInput(Small, sha: sha)));

        await AssertInvalid(service.ChunkInitAsync(code, InitInput(Small, size: -1)));

        // 业务路径的 subPath 不合法,不用等传完整个文件才发现
        var path = await Rejected(service.ChunkInitAsync(business, InitInput(Small, subPath: "../x")));
        Assert.Equal(ErrorCode.FilePathInvalidPath, path.Code);

        Assert.Empty(Directory.GetFileSystemEntries(env.Staging));
    }

    [Fact]
    public async Task Sha256_is_trimmed_and_case_insensitive()
    {
        await using var env = NewEnv(new MutableUser(1));
        var service = env.Service<IFilePathUploadService>();
        var (_, code) = await AddPathAsync(env);

        var init = await service.ChunkInitAsync(code, InitInput(Small, sha: $"  {Sha(Small).ToUpperInvariant()}\t"));
        using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(SessionDir(env, init.UploadId), "session.json"))))
            Assert.Equal(Sha(Small), doc.RootElement.GetProperty("Sha256").GetString());
        await UploadChunk(service, code, init.UploadId, 0, Small);
        var output = await service.ChunkCompleteAsync(code, init.UploadId);

        Assert.True(File.Exists(DiskPath(env, output.Path)));
    }

    [Fact]
    public async Task Chunk_count_rounds_up_and_empty_file_needs_no_chunk()
    {
        await using var env = NewEnv(new MutableUser(1));
        var service = env.Service<IFilePathUploadService>();
        var (_, code) = await AddPathAsync(env);

        foreach (var (size, count) in new (long, int)[] { (1, 1), (ChunkSize, 1), (ChunkSize + 1, 2), (2L * ChunkSize, 2) })
        {
            var init = await service.ChunkInitAsync(code, new("a.pdf", size, Sha(Small), null));
            Assert.Equal(count, init.ChunkCount);
            Assert.Equal(ChunkSize, init.ChunkSize);
        }

        // 空文件:0 片,直接合并出一个空文件
        var empty = await service.ChunkInitAsync(code, InitInput([]));
        Assert.Equal(0, empty.ChunkCount);
        var output = await service.ChunkCompleteAsync(code, empty.UploadId);
        Assert.Equal(0, new FileInfo(DiskPath(env, output.Path)).Length);
    }

    [Fact]
    public async Task Session_json_holds_only_the_listed_fields()
    {
        var user = new MutableUser(7);
        await using var env = NewEnv(user);
        var service = env.Service<IFilePathUploadService>();
        var (_, code) = await AddPathAsync(env, FilePathNamingRule.BusinessPath);

        var init = await service.ChunkInitAsync(code, InitInput(Big, subPath: "模板/A"));

        using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(SessionDir(env, init.UploadId), "session.json"))))
        {
            var root = doc.RootElement;
            Assert.Equal(
                ["ChunkCount", "ChunkSize", "Code", "FileName", "Sha256", "Size", "SubPath", "UserId"],
                root.EnumerateObject().Select(p => p.Name).Order().ToArray());
            Assert.Equal(7, root.GetProperty("UserId").GetInt64());
            Assert.Equal(code, root.GetProperty("Code").GetString());
            Assert.Equal("大文件.pdf", root.GetProperty("FileName").GetString());
            Assert.Equal(Big.Length, root.GetProperty("Size").GetInt64());
            Assert.Equal(Sha(Big), root.GetProperty("Sha256").GetString());
            Assert.Equal("模板/A", root.GetProperty("SubPath").GetString());
            Assert.Equal(ChunkSize, root.GetProperty("ChunkSize").GetInt32());
            Assert.Equal(2, root.GetProperty("ChunkCount").GetInt32());
        }

        // 没有登录用户的会话绑定 0
        user.UserId = null;
        var anonymous = await service.ChunkInitAsync(code, InitInput(Small));
        using var anonymousDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(SessionDir(env, anonymous.UploadId), "session.json")));
        Assert.Equal(0, anonymousDoc.RootElement.GetProperty("UserId").GetInt64());
    }

    [Fact]
    public async Task Full_flow_writes_file_and_clears_staging()
    {
        await using var env = NewEnv(new MutableUser(7));
        var service = env.Service<IFilePathUploadService>();
        var (_, code) = await AddPathAsync(env);

        var init = await service.ChunkInitAsync(code, InitInput(Big));
        Assert.Equal(ChunkSize, init.ChunkSize);
        Assert.Equal(2, init.ChunkCount);
        Assert.Matches("^[0-9a-f]{32}$", init.UploadId);
        Assert.True(Directory.Exists(SessionDir(env, init.UploadId)));

        await UploadChunk(service, code, init.UploadId, 0, Big);
        await UploadChunk(service, code, init.UploadId, 1, Big);
        var output = await service.ChunkCompleteAsync(code, init.UploadId);

        Assert.Matches(@"^\d{4}/\d{2}/大文件_[0-9a-f]{8}\.pdf$", output.Path);
        Assert.Equal("大文件.pdf", output.Name);
        Assert.Equal(Big.Length, output.Size);
        await AssertSameBytes(Big, DiskPath(env, output.Path));
        Assert.Empty(Directory.GetFileSystemEntries(env.Staging));

        // 会话随合并一起删了:再合并、再传片都是无效会话
        await AssertInvalid(service.ChunkCompleteAsync(code, init.UploadId));
        await AssertInvalid(UploadChunk(service, code, init.UploadId, 0, Big));
    }

    [Fact]
    public async Task Business_path_complete_uses_sub_path_and_reports_existing_file()
    {
        await using var env = NewEnv(new MutableUser(1));
        var service = env.Service<IFilePathUploadService>();
        var (_, code) = await AddPathAsync(env, FilePathNamingRule.BusinessPath);

        var output = await UploadWholeAsync(service, code, Small, "标签.pdf", subPath: "模板/A");
        Assert.Equal("模板/A/标签.pdf", output.Path);

        // 业务指定的路径撞名是调用方要知道的结果;失败也不留暂存
        var ex = await Rejected(UploadWholeAsync(service, code, Small, "标签.pdf", subPath: "模板/A"));
        Assert.Equal(ErrorCode.FilePathFileExists, ex.Code);
        Assert.Empty(Directory.GetFileSystemEntries(env.Staging));
    }

    [Fact]
    public async Task Missing_chunk_reports_index_and_keeps_the_session()
    {
        await using var env = NewEnv(new MutableUser(1));
        var service = env.Service<IFilePathUploadService>();
        var (_, code) = await AddPathAsync(env);
        var init = await service.ChunkInitAsync(code, InitInput(Big));

        var none = await Rejected(service.ChunkCompleteAsync(code, init.UploadId));
        Assert.Equal(ErrorCode.FilePathChunkMissing, none.Code);
        Assert.Equal(0, none.Args!["index"]);

        await UploadChunk(service, code, init.UploadId, 0, Big);
        var ex = await Rejected(service.ChunkCompleteAsync(code, init.UploadId));
        Assert.Equal(ErrorCode.FilePathChunkMissing, ex.Code);
        Assert.Equal(1, ex.Args!["index"]);

        // 缺片不毁掉会话:补传空缺后可以再合并
        await UploadChunk(service, code, init.UploadId, 1, Big);
        var output = await service.ChunkCompleteAsync(code, init.UploadId);
        await AssertSameBytes(Big, DiskPath(env, output.Path));
    }

    [Fact]
    public async Task Hash_mismatch_is_rejected_and_nothing_is_written()
    {
        await using var env = NewEnv(new MutableUser(1));
        var service = env.Service<IFilePathUploadService>();
        var (_, code) = await AddPathAsync(env);
        var init = await service.ChunkInitAsync(code, InitInput(Big, sha: Sha(Small)));
        await UploadChunk(service, code, init.UploadId, 0, Big);
        await UploadChunk(service, code, init.UploadId, 1, Big);

        var ex = await Rejected(service.ChunkCompleteAsync(code, init.UploadId));

        Assert.Equal(ErrorCode.FilePathChunkHashMismatch, ex.Code);
        Assert.Empty(Directory.EnumerateFiles(env.Root, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFileSystemEntries(env.Staging));
    }

    [Fact]
    public async Task Chunk_index_and_length_are_checked()
    {
        await using var env = NewEnv(new MutableUser(1));
        var service = env.Service<IFilePathUploadService>();
        var (_, code) = await AddPathAsync(env);
        var init = await service.ChunkInitAsync(code, InitInput(Big));
        var dir = SessionDir(env, init.UploadId);
        Task Send(int index, int length) => service.ChunkAsync(code, init.UploadId, index, new MemoryStream(new byte[length]));

        // 编号越界
        await AssertInvalid(Send(2, 10));
        await AssertInvalid(Send(-1, 10));

        // 非末片必须正好 ChunkSize,末片必须正好是剩下的 2 MB;多一个字节、少一个字节都不行
        await AssertInvalid(Send(0, 1000));
        await AssertInvalid(Send(0, ChunkSize - 1));
        await AssertInvalid(Send(0, ChunkSize + 1));
        await AssertInvalid(Send(1, TwoMb - 1));
        await AssertInvalid(Send(1, TwoMb + 1));
        await AssertInvalid(Send(1, ChunkSize));

        // 拒绝时既没有 .part,也没有写到一半的临时文件
        Assert.Equal(["session.json"], FileNames(dir));

        // 坏分片不毁掉会话,正确的分片照常收
        await UploadChunk(service, code, init.UploadId, 0, Big);
        await UploadChunk(service, code, init.UploadId, 1, Big);
        Assert.Equal(["0.part", "1.part", "session.json"], FileNames(dir));
    }

    [Fact]
    public async Task Over_long_chunk_stops_reading_and_leaves_nothing()
    {
        await using var env = NewEnv(new MutableUser(1));
        var service = env.Service<IFilePathUploadService>();
        var (_, code) = await AddPathAsync(env);
        var init = await service.ChunkInitAsync(code, InitInput(Big));
        var endless = new EndlessStream();

        // 读不完的流:服务端读到应有长度之外就得停下,而不是把流读干
        await AssertInvalid(service.ChunkAsync(code, init.UploadId, 1, endless));

        Assert.InRange(endless.BytesRead, TwoMb + 1, TwoMb + 1024 * 1024);
        Assert.Equal(["session.json"], FileNames(SessionDir(env, init.UploadId)));
    }

    [Fact]
    public async Task Retransmitted_chunk_overwrites_and_a_failed_retransmit_keeps_the_old_one()
    {
        await using var env = NewEnv(new MutableUser(1));
        var service = env.Service<IFilePathUploadService>();
        var (_, code) = await AddPathAsync(env);
        var init = await service.ChunkInitAsync(code, InitInput(Big));
        var dir = SessionDir(env, init.UploadId);
        await UploadChunk(service, code, init.UploadId, 0, Big);

        // 第 1 片先传一份同样长度的错误内容,再重传正确的:覆盖
        await service.ChunkAsync(code, init.UploadId, 1, new MemoryStream(new byte[TwoMb]));
        await UploadChunk(service, code, init.UploadId, 1, Big);

        // 重传到一半断开:上一次的分片原样保留,不留临时文件
        await Assert.ThrowsAsync<IOException>(() => service.ChunkAsync(code, init.UploadId, 1, new DroppedStream(1000)));
        Assert.Equal(["0.part", "1.part", "session.json"], FileNames(dir));

        var output = await service.ChunkCompleteAsync(code, init.UploadId);
        await AssertSameBytes(Big, DiskPath(env, output.Path));
    }

    [Fact]
    public async Task Forged_upload_id_is_rejected()
    {
        await using var env = NewEnv(new MutableUser(1));
        var service = env.Service<IFilePathUploadService>();
        var (_, code) = await AddPathAsync(env);
        var real = (await service.ChunkInitAsync(code, InitInput(Small))).UploadId;

        // 诱饵:暂存目录的兄弟目录里放一个绑定同一用户和编码的「合法」会话。
        // 能跳出暂存目录的 Id 一旦被放行,分片就会写进它,合并就不会报「无效会话」
        var parent = Path.GetDirectoryName(env.Staging)!;
        var decoyName = $"smart-fp-decoy-{Guid.NewGuid():N}";
        var decoy = Directory.CreateDirectory(Path.Combine(parent, decoyName)).FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(decoy, "session.json"), JsonSerializer.Serialize(new
            {
                UserId = 1L, Code = code, FileName = "a.pdf", Size = Small.Length, Sha256 = Sha(Small),
                SubPath = (string?)null, ChunkSize, ChunkCount = 1,
            }));

            var forged = new[]
            {
                $"../{decoyName}", $@"..\{decoyName}", $@"..\..\{Path.GetFileName(parent)}\{decoyName}", "../x", @"..\..\x",
                real.ToUpperInvariant(), real[..31], real + "0", real + "\n", real + "/", $"{real}/../{real}", "", " ",
            };
            foreach (var id in forged)
            {
                await AssertInvalid(service.ChunkAsync(code, id, 0, new MemoryStream(Small)));
                await AssertInvalid(service.ChunkCompleteAsync(code, id));
            }

            // 诱饵和真会话都没被碰过,暂存目录的父目录里也没有多出别的条目
            Assert.Equal(["session.json"], FileNames(decoy));
            Assert.Equal(["session.json"], FileNames(SessionDir(env, real)));
            Assert.Equal([decoyName], Directory.GetDirectories(parent, decoyName + "*").Select(d => Path.GetFileName(d)).ToArray());
        }
        finally
        {
            Directory.Delete(decoy, recursive: true);
        }
    }

    [Fact]
    public async Task Upload_id_is_bound_to_user_and_code()
    {
        var user = new MutableUser(1);
        await using var env = NewEnv(user);
        var service = env.Service<IFilePathUploadService>();
        var (_, code) = await AddPathAsync(env);
        var (_, other) = await AddPathAsync(env);
        var init = await service.ChunkInitAsync(code, InitInput(Small));

        // 别的用户(含未登录)拿到 Id 也续传、合并不了
        foreach (long? stranger in new long?[] { 2, null })
        {
            user.UserId = stranger;
            await AssertInvalid(UploadChunk(service, code, init.UploadId, 0, Small));
            await AssertInvalid(service.ChunkCompleteAsync(code, init.UploadId));
        }

        // 同一个用户换一个编码也不行
        user.UserId = 1;
        await AssertInvalid(UploadChunk(service, other, init.UploadId, 0, Small));
        await AssertInvalid(service.ChunkCompleteAsync(other, init.UploadId));
        Assert.Equal(["session.json"], FileNames(SessionDir(env, init.UploadId)));

        // 原用户原编码照常完成
        await UploadChunk(service, code, init.UploadId, 0, Small);
        var output = await service.ChunkCompleteAsync(code, init.UploadId);
        Assert.True(File.Exists(DiskPath(env, output.Path)));
    }

    [Fact]
    public async Task Chunk_and_complete_resolve_the_rule_again()
    {
        await using var env = NewEnv(new MutableUser(1));
        var service = env.Service<IFilePathUploadService>();
        var (id, code) = await AddPathAsync(env);
        var init = await service.ChunkInitAsync(code, InitInput(Small));

        // init 之后这条路径被停用:后续的分片和合并都经解析器重新判断,不凭会话里的旧信息放行
        await ReconfigureAsync(env, id, enabled: false);

        Assert.Equal(ErrorCode.FilePathNotFound, (await Rejected(UploadChunk(service, code, init.UploadId, 0, Small))).Code);
        Assert.Equal(ErrorCode.FilePathNotFound, (await Rejected(service.ChunkCompleteAsync(code, init.UploadId))).Code);
        Assert.Empty(Directory.EnumerateFiles(env.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Complete_validates_the_actual_size_against_the_current_rule()
    {
        await using var env = NewEnv(new MutableUser(1));
        var service = env.Service<IFilePathUploadService>();
        var (id, code) = await AddPathAsync(env);
        var init = await service.ChunkInitAsync(code, InitInput(Big));
        await UploadChunk(service, code, init.UploadId, 0, Big);
        await UploadChunk(service, code, init.UploadId, 1, Big);

        // init 之后上限被调小到 5 MB:合并时按实际大小和当前规则再拦一次
        await ReconfigureAsync(env, id, maxSizeMb: 5);
        var ex = await Rejected(service.ChunkCompleteAsync(code, init.UploadId));

        Assert.Equal(ErrorCode.FilePathTooLarge, ex.Code);
        Assert.Equal(5, ex.Args!["maxSizeMb"]);
        Assert.Empty(Directory.EnumerateFiles(env.Root, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFileSystemEntries(env.Staging));
    }

    // ---- 清扫器 ----

    private static string NewSession(string staging, DateTime? lastWriteUtc = null, string? name = null)
    {
        var dir = Directory.CreateDirectory(Path.Combine(staging, name ?? Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(dir, "session.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "0.part"), "x");
        // 放在写文件之后:往目录里建文件会刷新它的最后写入时间
        if (lastWriteUtc is { } t) Directory.SetLastWriteTimeUtc(dir, t);
        return dir;
    }

    private static AdminFilePathOptions Options(string staging, int ttlHours = 24) =>
        new() { StagingPath = staging, StagingTtlHours = ttlHours };

    private static FilePathStagingSweeper NewSweeper(string staging, int ttlHours = 24) =>
        new(Options(staging, ttlHours), TimeProvider.System, NullLogger<FilePathStagingSweeper>.Instance);

    private static DateTime HoursAgo(int hours) => DateTime.UtcNow.AddHours(-hours);

    [Fact]
    public async Task Sweeper_removes_expired_sessions_only()
    {
        await using var env = NewEnv(new MutableUser(1));
        var expired = NewSession(env.Staging, HoursAgo(25));
        var fresh = NewSession(env.Staging, HoursAgo(23));

        Assert.Equal(1, NewSweeper(env.Staging).SweepOnce());

        Assert.False(Directory.Exists(expired));
        Assert.True(Directory.Exists(fresh));
        Assert.Equal(0, NewSweeper(env.Staging).SweepOnce());
    }

    [Fact]
    public async Task Sweeper_uses_the_configured_ttl()
    {
        await using var env = NewEnv(new MutableUser(1));
        var session = NewSession(env.Staging, HoursAgo(25));

        Assert.Equal(0, NewSweeper(env.Staging, ttlHours: 48).SweepOnce());
        Assert.True(Directory.Exists(session));

        Assert.Equal(1, NewSweeper(env.Staging, ttlHours: 24).SweepOnce());
        Assert.False(Directory.Exists(session));
    }

    [Fact]
    public async Task Sweeper_ignores_names_that_are_not_session_ids()
    {
        await using var env = NewEnv(new MutableUser(1));
        var id = Guid.NewGuid().ToString("N");
        var kept = new[] { "keep-me", id.ToUpperInvariant(), id[..31], id + "0", "x" + id[1..] }
            .Select(name => NewSession(env.Staging, HoursAgo(100), name)).ToList();
        // 名字合规但是文件不是目录:不碰
        var file = Path.Combine(env.Staging, Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(file, "x");
        File.SetLastWriteTimeUtc(file, HoursAgo(100));

        Assert.Equal(0, NewSweeper(env.Staging).SweepOnce());

        Assert.All(kept, dir => Assert.True(Directory.Exists(dir)));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Sweeper_returns_zero_when_staging_does_not_exist()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"smart-fp-missing-{Guid.NewGuid():N}");

        Assert.Equal(0, NewSweeper(missing).SweepOnce());
        Assert.False(Directory.Exists(missing));
    }

    /// <summary>按目录名指定删除结果:抛指定异常,或交给真实删除。</summary>
    private sealed class FlakySweeper(AdminFilePathOptions options, Dictionary<string, Exception> failures)
        : FilePathStagingSweeper(options, TimeProvider.System, NullLogger<FilePathStagingSweeper>.Instance)
    {
        protected override void DeleteSession(string dir)
        {
            if (failures.TryGetValue(Path.GetFileName(dir), out var failure)) throw failure;
            base.DeleteSession(dir);
        }
    }

    [Fact]
    public async Task Sweeper_goes_on_after_a_failed_or_vanished_delete()
    {
        await using var env = NewEnv(new MutableUser(1));
        var locked = NewSession(env.Staging, HoursAgo(30));
        var vanished = NewSession(env.Staging, HoursAgo(30));
        var removable = NewSession(env.Staging, HoursAgo(30));
        var sweeper = new FlakySweeper(Options(env.Staging), new()
        {
            [Path.GetFileName(locked)] = new IOException("占用中"),
            // 多副本共用暂存卷:别的副本先删掉了
            [Path.GetFileName(vanished)] = new DirectoryNotFoundException(),
        });

        // 失败的和已经没了的都不计数,也不打断其余的
        Assert.Equal(1, sweeper.SweepOnce());

        Assert.False(Directory.Exists(removable));
        Assert.True(Directory.Exists(locked));
    }

    [Fact]
    public async Task Sweeper_is_registered_once_as_a_hosted_service()
    {
        await using var env = NewEnv(new MutableUser(1));
        var hosted = env.Factory.Services.GetServices<IHostedService>().OfType<FilePathStagingSweeper>().ToList();

        Assert.Single(hosted);
        Assert.Same(env.Factory.Services.GetRequiredService<FilePathStagingSweeper>(), hosted[0]);
    }
}
