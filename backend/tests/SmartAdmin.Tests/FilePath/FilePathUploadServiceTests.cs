using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>
/// 文件路径上传:只读/后缀/大小校验、三种命名规则、撞名与覆盖(含失败回滚、并发覆盖)、删除的隐藏段处理。
/// 服务用固定时钟(本地时间 2026-10-20 10:00)手工构造,每个用例的文件落在各自的临时根目录里。
/// </summary>
public class FilePathUploadServiceTests
{
    private static readonly DateTimeOffset Fixed = new(new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Local));

    private static string NewCode() => $"u-{Guid.NewGuid():N}";

    private static async Task<AdminException> Rejected(Task task) => await Assert.ThrowsAsync<AdminException>(() => task);

    private static FilePathUploadService NewService(FilePathTestEnv env) =>
        new(env.Service<IFilePathResolver>(), new FakeTimeProvider(Fixed), NullLogger<FilePathUploadService>.Instance);

    private static async Task<string> AddPathAsync(
        FilePathTestEnv env, FilePathNamingRule naming = FilePathNamingRule.YearMonthOriginal,
        FilePathConflictMode onConflict = FilePathConflictMode.Reject, bool readOnly = false,
        string? extensions = ".pdf", int? maxSizeMb = 10, IReadOnlyList<long>? upload = null)
    {
        var code = NewCode();
        await env.Service<IFilePathConfigService>().AddAsync(new FilePathAddInput(
            code, "订单附件", await env.AddServerAsync(), "Docs", naming, onConflict, readOnly,
            readOnly ? null : extensions, readOnly ? null : maxSizeMb, upload, null, true, null));
        return code;
    }

    private static FilePathUploadInput Upload(string fileName, string content = "内容", string? subPath = null, long? size = null)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FilePathUploadInput(new MemoryStream(bytes), fileName, size ?? bytes.Length, subPath);
    }

    private static string DiskPath(FilePathTestEnv env, string relative) => Path.Combine(env.Root, "Docs", relative);

    private static string[] TempFiles(FilePathTestEnv env) =>
        Directory.Exists(Path.Combine(env.Root, "Docs"))
            ? [.. Directory.EnumerateFiles(Path.Combine(env.Root, "Docs"), "*", SearchOption.AllDirectories).Where(f => f.Contains(".uploading-"))]
            : [];

    /// <summary>可预测的短 ID,用来制造撞名。</summary>
    private sealed class FixedIdService(IFilePathResolver resolver, params string[] ids)
        : FilePathUploadService(resolver, new FakeTimeProvider(Fixed), NullLogger<FilePathUploadService>.Instance)
    {
        private int _next;

        protected override string NewShortId() => ids[_next++];
    }

    /// <summary>读文件属性时抛指定异常:模拟共享断开、权限丢失(File.Exists 会把这些都报成「不存在」)。</summary>
    private sealed class UnreachableService(IFilePathResolver resolver, Exception failure)
        : FilePathUploadService(resolver, new FakeTimeProvider(Fixed), NullLogger<FilePathUploadService>.Instance)
    {
        protected override FileAttributes GetAttributes(string fullPath) => throw failure;
    }

    /// <summary>先吐出一段内容,再抛 <see cref="IOException"/>:模拟传输中途断开。</summary>
    private sealed class BrokenStream : Stream
    {
        private bool _served;

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
            if (_served) throw new IOException("连接中断");
            _served = true;
            buffer.Span[..4].Fill((byte)'x');
            return ValueTask.FromResult(4);
        }
    }

    [Fact]
    public async Task Year_month_original_naming()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);

        var output = await NewService(env).UploadAsync(code, Upload("报价单.pdf", "你好"));

        Assert.Matches(@"^2026/10/报价单_[0-9a-f]{8}\.pdf$", output.Path);
        Assert.Equal("报价单.pdf", output.Name);
        Assert.Equal(Encoding.UTF8.GetByteCount("你好"), output.Size);
        Assert.Equal("你好", await File.ReadAllTextAsync(DiskPath(env, output.Path)));
        Assert.Null(output.ViewUrl);
    }

    [Fact]
    public async Task Year_month_collision_retries_with_new_id()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        var service = new FixedIdService(env.Service<IFilePathResolver>(), "aaaaaaaa", "aaaaaaaa", "bbbbbbbb");

        var first = await service.UploadAsync(code, Upload("a.pdf", "一"));
        var second = await service.UploadAsync(code, Upload("a.pdf", "二"));

        Assert.Equal("2026/10/a_aaaaaaaa.pdf", first.Path);
        Assert.Equal("2026/10/a_bbbbbbbb.pdf", second.Path);
        Assert.Equal("一", await File.ReadAllTextAsync(DiskPath(env, first.Path)));
        Assert.Equal("二", await File.ReadAllTextAsync(DiskPath(env, second.Path)));
    }

    [Fact]
    public async Task Year_month_collision_that_repeats_is_reported()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        var service = new FixedIdService(env.Service<IFilePathResolver>(), "aaaaaaaa", "aaaaaaaa", "aaaaaaaa");
        await service.UploadAsync(code, Upload("a.pdf"));

        // 只重试一次;第二次还撞就如实报告,不无限循环
        var ex = await Rejected(service.UploadAsync(code, Upload("a.pdf")));

        Assert.Equal(ErrorCode.FilePathFileExists, ex.Code);
    }

    [Fact]
    public async Task Date_guid_naming()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env, FilePathNamingRule.DateGuid);

        var output = await NewService(env).UploadAsync(code, Upload("报价单.pdf"));

        Assert.Matches(@"^20261020/[0-9a-f]{32}\.pdf$", output.Path);
        Assert.True(File.Exists(DiskPath(env, output.Path)));
    }

    [Fact]
    public async Task Business_path_uses_sanitized_sub_path()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env, FilePathNamingRule.BusinessPath, extensions: ".btw");

        var output = await NewService(env).UploadAsync(code, Upload("标签.btw", subPath: "模板/A:B"));

        Assert.Equal("模板/A_B/标签.btw", output.Path);
        Assert.True(File.Exists(DiskPath(env, "模板/A_B/标签.btw")));

        // 没有 subPath 时直接落在文件夹下;反斜杠和正斜杠等价
        Assert.Equal("标签.btw", (await NewService(env).UploadAsync(code, Upload("标签.btw"))).Path);
        Assert.Equal("x/y/标签.btw", (await NewService(env).UploadAsync(code, Upload("标签.btw", subPath: @"x\y"))).Path);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/../../x")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("/abs")]
    [InlineData(@"\\nas01\files")]
    [InlineData("a//b")]
    [InlineData("a/")]
    [InlineData("a/ /b")]
    [InlineData("a/.. /b")]
    [InlineData("a/.../b")]
    public async Task Business_path_rejects_traversal_or_blank_sub_path(string subPath)
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env, FilePathNamingRule.BusinessPath);

        var ex = await Rejected(NewService(env).UploadAsync(code, Upload("a.pdf", subPath: subPath)));

        Assert.Equal(ErrorCode.FilePathInvalidPath, ex.Code);
        Assert.False(File.Exists(Path.Combine(env.Root, "x", "a.pdf")));
    }

    [Fact]
    public async Task Business_path_rejects_sub_path_segment_over_255_bytes()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env, FilePathNamingRule.BusinessPath);
        var service = NewService(env);

        // 清洗只截主干,点后面很长的「后缀」不会被截;而 subPath 的段不经后缀白名单
        var longTail = "x." + new string('y', 300);
        var ex = await Rejected(service.UploadAsync(code, Upload("a.pdf", subPath: $"ok/{longTail}")));
        Assert.Equal(ErrorCode.FilePathInvalidPath, ex.Code);

        // 按字节而不是字符数:100 个汉字清洗后截成 60 个(180 字节),放得下
        var output = await service.UploadAsync(code, Upload("a.pdf", subPath: new string('汉', 100)));
        Assert.Equal(new string('汉', 60) + "/a.pdf", output.Path);
    }

    [Fact]
    public async Task Reject_mode_reports_existing_file()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env, FilePathNamingRule.BusinessPath);
        var service = NewService(env);
        await service.UploadAsync(code, Upload("a.pdf", "旧"));

        var ex = await Rejected(service.UploadAsync(code, Upload("a.pdf", "新")));

        Assert.Equal(ErrorCode.FilePathFileExists, ex.Code);
        Assert.Equal("旧", await File.ReadAllTextAsync(DiskPath(env, "a.pdf")));
        Assert.Empty(TempFiles(env));
    }

    [Fact]
    public async Task Reject_mode_failure_leaves_no_partial_file()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env, FilePathNamingRule.BusinessPath);
        var service = NewService(env);

        var ex = await Rejected(service.UploadAsync(code, new FilePathUploadInput(new BrokenStream(), "a.pdf", 8, null)));
        Assert.Equal(ErrorCode.FilePathFolderUnavailable, ex.Code);
        Assert.False(File.Exists(DiskPath(env, "a.pdf")));

        // 半截文件没留下,同名上传不会被误报「文件已存在」
        await service.UploadAsync(code, Upload("a.pdf", "好"));
        Assert.Equal("好", await File.ReadAllTextAsync(DiskPath(env, "a.pdf")));
    }

    [Fact]
    public async Task Overwrite_mode_replaces_content()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env, FilePathNamingRule.BusinessPath, FilePathConflictMode.Overwrite);
        var service = NewService(env);
        await service.UploadAsync(code, Upload("a.pdf", "旧"));

        var output = await service.UploadAsync(code, Upload("a.pdf", "新"));

        Assert.Equal("a.pdf", output.Path);
        Assert.Equal("新", await File.ReadAllTextAsync(DiskPath(env, "a.pdf")));
        Assert.Empty(TempFiles(env));
    }

    [Fact]
    public async Task Overwrite_failure_keeps_old_file()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env, FilePathNamingRule.BusinessPath, FilePathConflictMode.Overwrite);
        var service = NewService(env);
        await service.UploadAsync(code, Upload("a.pdf", "旧"));

        var ex = await Rejected(service.UploadAsync(code, new FilePathUploadInput(new BrokenStream(), "a.pdf", 8, null)));

        Assert.Equal(ErrorCode.FilePathFolderUnavailable, ex.Code);
        Assert.IsAssignableFrom<IOException>(ex.InnerException);
        Assert.Equal("旧", await File.ReadAllTextAsync(DiskPath(env, "a.pdf")));
        Assert.Empty(TempFiles(env));
    }

    [Fact]
    public async Task Concurrent_overwrites_leave_no_temp_files()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env, FilePathNamingRule.BusinessPath, FilePathConflictMode.Overwrite);
        await NewService(env).UploadAsync(code, Upload("a.pdf", "初始"));

        var contents = Enumerable.Range(0, 10).Select(i => $"第 {i} 份").ToArray();
        // 每个并行任务用自己的 scope,免得共享同一个仓储实例
        var services = contents.Select(_ => NewService(env)).ToArray();
        await Task.WhenAll(contents.Select((c, i) => Task.Run(() => services[i].UploadAsync(code, Upload("a.pdf", c)))));

        Assert.Contains(await File.ReadAllTextAsync(DiskPath(env, "a.pdf")), contents);
        Assert.Empty(TempFiles(env));
    }

    [Fact]
    public async Task Read_only_rejects_upload()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env, readOnly: true);

        var ex = await Rejected(NewService(env).UploadAsync(code, Upload("a.pdf")));

        Assert.Equal(ErrorCode.FilePathReadOnly, ex.Code);
    }

    [Fact]
    public async Task Extension_case_and_missing_extension()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        var service = NewService(env);

        var output = await service.UploadAsync(code, Upload("报价.PDF"));
        Assert.EndsWith(".pdf", output.Path);
        Assert.True(File.Exists(DiskPath(env, output.Path)));
        Assert.Equal("报价.PDF", output.Name);

        var none = await Rejected(service.UploadAsync(code, Upload("README")));
        Assert.Equal(ErrorCode.FilePathExtNotAllowed, none.Code);
        Assert.Equal("", none.Args!["ext"]);

        var other = await Rejected(service.UploadAsync(code, Upload("a.EXE")));
        Assert.Equal(ErrorCode.FilePathExtNotAllowed, other.Code);
        Assert.Equal(".exe", other.Args!["ext"]);

        // 后缀取清洗后的文件名:路径段不算,末尾的点被去掉后也可能没有后缀
        Assert.Equal(ErrorCode.FilePathExtNotAllowed, (await Rejected(service.UploadAsync(code, Upload("x.pdf/README")))).Code);
        Assert.Equal(ErrorCode.FilePathExtNotAllowed, (await Rejected(service.UploadAsync(code, Upload("a.")))).Code);
    }

    [Fact]
    public async Task Too_large_is_rejected()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env, maxSizeMb: 1);
        var service = NewService(env);

        var ex = await Rejected(service.UploadAsync(code, Upload("a.pdf", size: 1024 * 1024 + 1)));
        Assert.Equal(ErrorCode.FilePathTooLarge, ex.Code);
        Assert.Equal(1, ex.Args!["maxSizeMb"]);

        // 正好等于上限放行
        await service.UploadAsync(code, Upload("a.pdf", size: 1024 * 1024));
    }

    [Fact]
    public async Task Size_limit_does_not_overflow_for_huge_limits()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env, maxSizeMb: int.MaxValue);

        // int.MaxValue MB 用 int 乘会溢出成负数,把所有文件都拒掉
        var output = await NewService(env).UploadAsync(code, Upload("a.pdf", size: 3L * 1024 * 1024 * 1024));

        Assert.Equal(3L * 1024 * 1024 * 1024, output.Size);
    }

    [Fact]
    public async Task Writable_row_without_extensions_or_limit_is_unusable()
    {
        await using var env = FilePathTestEnv.Create();
        var serverId = await env.AddServerAsync();
        // 保存入口写不出这种行,手工改库才有;不能当作「不限」
        var (_, noExt) = await env.AddRawPathAsync(serverId, null, null, extensions: null);
        var (_, noSize) = await env.AddRawPathAsync(serverId, null, null, maxSizeMb: null);
        var service = NewService(env);

        Assert.Equal(ErrorCode.FilePathRuleIncomplete, (await Rejected(service.UploadAsync(noExt, Upload("a.pdf")))).Code);
        Assert.Equal(ErrorCode.FilePathRuleIncomplete, (await Rejected(service.UploadAsync(noSize, Upload("a.pdf")))).Code);
    }

    [Fact]
    public async Task Delete_removes_file_and_rejects_hidden_or_missing()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        var service = NewService(env);
        var output = await service.UploadAsync(code, Upload("a.pdf"));
        var hidden = DiskPath(env, ".x/a.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(hidden)!);
        await File.WriteAllTextAsync(hidden, "探针");

        await service.DeleteAsync(code, output.Path);
        Assert.False(File.Exists(DiskPath(env, output.Path)));

        // 隐藏段、不存在、不合法的路径一律是「文件不存在」,隐藏文件原样保留
        foreach (var path in new[] { ".x/a.pdf", @".x\a.pdf", "2026/10/none.pdf", "no-such-dir/none.pdf", "../x.pdf", "a:b.pdf", "" })
            Assert.Equal(ErrorCode.FilePathFileNotFound, (await Rejected(service.DeleteAsync(code, path))).Code);
        Assert.True(File.Exists(hidden));
    }

    [Fact]
    public async Task Delete_of_a_folder_is_file_not_found_and_keeps_it()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        var service = NewService(env);
        var output = await service.UploadAsync(code, Upload("a.pdf"));
        var folder = Path.GetDirectoryName(DiskPath(env, output.Path))!;

        // 路径指向目录:不是文件,不删,里面的文件也还在
        var ex = await Rejected(service.DeleteAsync(code, "2026/10"));

        Assert.Equal(ErrorCode.FilePathFileNotFound, ex.Code);
        Assert.True(Directory.Exists(folder));
        Assert.True(File.Exists(DiskPath(env, output.Path)));
    }

    [Fact]
    public async Task Delete_reports_access_failure_instead_of_missing_file()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        var file = DiskPath(env, "a.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "还在");
        var resolver = env.Service<IFilePathResolver>();

        // 共享断开 / 权限丢失不能报成「文件不存在」:业务会据此以为已经删了,NAS 上留下孤儿文件
        var offline = await Rejected(new UnreachableService(resolver, new IOException("共享已断开", unchecked((int)0x80070035)))
            .DeleteAsync(code, "a.pdf"));
        Assert.Equal(ErrorCode.FilePathFolderUnavailable, offline.Code);
        Assert.Equal(FileAccessFailure.NetworkPathNotFound, offline.Args!["reason"]);

        var denied = await Rejected(new UnreachableService(resolver, new UnauthorizedAccessException())
            .DeleteAsync(code, "a.pdf"));
        Assert.Equal(ErrorCode.FilePathFolderUnavailable, denied.Code);
        Assert.Equal(FileAccessFailure.AccessDenied, denied.Args!["reason"]);

        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Delete_is_refused_on_read_only_row()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env, readOnly: true);
        var file = DiskPath(env, "old.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "存档");

        var ex = await Rejected(NewService(env).DeleteAsync(code, "old.pdf"));

        Assert.Equal(ErrorCode.FilePathReadOnly, ex.Code);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Backend_call_skips_role_check()
    {
        await using var env = FilePathTestEnv.Create();
        // 上传角色只有 999,而调用方没有任何登录用户:服务本身不判角色
        var code = await AddPathAsync(env, upload: [999]);

        var output = await NewService(env).UploadAsync(code, Upload("a.pdf"));

        Assert.True(File.Exists(DiskPath(env, output.Path)));
    }

    [Fact]
    public async Task Disabled_or_missing_code_is_not_found()
    {
        await using var env = FilePathTestEnv.Create();
        var service = NewService(env);

        Assert.Equal(ErrorCode.FilePathNotFound, (await Rejected(service.UploadAsync("no-such", Upload("a.pdf")))).Code);
        Assert.Equal(ErrorCode.FilePathNotFound, (await Rejected(service.DeleteAsync("no-such", "a.pdf"))).Code);
    }
}
