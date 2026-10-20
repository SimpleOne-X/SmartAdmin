using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>
/// 文件路径下载:读出内容与元数据、路径处理的顺序(不合法 → 隐藏段 → 拼接)、不受上传后缀白名单约束、
/// 访问失败与「不存在」的区分,以及读着文件时覆盖上传仍能成功。
/// </summary>
public class FilePathDownloadServiceTests
{
    private static string NewCode() => $"d-{Guid.NewGuid():N}";

    private static async Task<AdminException> Rejected(Task task) => await Assert.ThrowsAsync<AdminException>(() => task);

    private static async Task<string> AddPathAsync(
        FilePathTestEnv env, FilePathNamingRule naming = FilePathNamingRule.YearMonthOriginal,
        FilePathConflictMode onConflict = FilePathConflictMode.Reject, bool readOnly = false)
    {
        var code = NewCode();
        await env.Service<IFilePathConfigService>().AddAsync(new FilePathAddInput(
            code, "订单附件", await env.AddServerAsync(), "Docs", naming, onConflict, readOnly,
            readOnly ? null : ".pdf", readOnly ? null : 10, null, null, true, null));
        return code;
    }

    private static FilePathUploadInput Upload(string fileName, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FilePathUploadInput(new MemoryStream(bytes), fileName, bytes.Length, null);
    }

    private static string DiskPath(FilePathTestEnv env, string relative) => Path.Combine(env.Root, "Docs", relative);

    private static async Task PutAsync(FilePathTestEnv env, string relative, string content)
    {
        var file = DiskPath(env, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, content);
    }

    private static async Task<string> ReadAllAsync(FilePathDownload download)
    {
        await using var content = download.Content;
        return await new StreamReader(content, Encoding.UTF8).ReadToEndAsync();
    }

    /// <summary>在「读属性」和「打开文件」两步各自按需抛指定异常:模拟共享断开、权限丢失、系统拒绝文件名。</summary>
    private sealed class FaultyService(FilePathTestEnv env, Exception? attributes = null, Exception? open = null)
        : FilePathDownloadService(env.Service<IFilePathResolver>(), NullLogger<FilePathDownloadService>.Instance)
    {
        protected override FileAttributes GetAttributes(string fullPath) =>
            attributes is null ? base.GetAttributes(fullPath) : throw attributes;

        protected override FileStream OpenRead(string fullPath) =>
            open is null ? base.OpenRead(fullPath) : throw open;
    }

    [Fact]
    public async Task Open_returns_stream_and_metadata()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        var output = await env.Service<IFilePathUploadService>().UploadAsync(code, Upload("报价单.pdf", "你好"));

        var download = await env.Service<IFilePathDownloadService>().OpenReadAsync(code, output.Path);

        Assert.Equal(Path.GetFileName(output.Path), download.FileName);
        Assert.Equal(".pdf", download.Extension);
        Assert.Equal(Encoding.UTF8.GetByteCount("你好"), download.Length);
        Assert.Equal("你好", await ReadAllAsync(download));
    }

    [Fact]
    public async Task Open_accepts_backslash_separators()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        await PutAsync(env, "a/b.pdf", "嵌套");

        var download = await env.Service<IFilePathDownloadService>().OpenReadAsync(code, @"a\b.pdf");

        Assert.Equal("b.pdf", download.FileName);
        Assert.Equal("嵌套", await ReadAllAsync(download));
    }

    [Fact]
    public async Task Hidden_segment_is_not_found()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        await PutAsync(env, ".smartadmin-probe-x", "探针");
        await PutAsync(env, ".x/a.pdf", "隐藏目录里的文件");
        var service = env.Service<IFilePathDownloadService>();

        // 文件确实存在,仍报「不存在」;反斜杠写法同样
        foreach (var path in new[] { ".smartadmin-probe-x", ".x/a.pdf", @".x\a.pdf" })
        {
            Assert.Equal(ErrorCode.FilePathFileNotFound, (await Rejected(service.OpenReadAsync(code, path))).Code);
            Assert.Equal(ErrorCode.FilePathFileNotFound, (await Rejected(service.GetFullPathAsync(code, path))).Code);
        }
    }

    [Fact]
    public async Task Download_ignores_allowed_extensions()
    {
        await using var env = FilePathTestEnv.Create();
        // 只读的行没有后缀白名单,存档里本来就有各种后缀;白名单只管上传
        var code = await AddPathAsync(env, readOnly: true);
        await PutAsync(env, "old.exe", "存档");

        var download = await env.Service<IFilePathDownloadService>().OpenReadAsync(code, "old.exe");

        Assert.Equal(".exe", download.Extension);
        Assert.Equal("存档", await ReadAllAsync(download));
    }

    [Fact]
    public async Task Traversal_is_rejected()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        await File.WriteAllTextAsync(Path.Combine(env.Root, "secret.txt"), "机密");
        var service = env.Service<IFilePathDownloadService>();

        // 含 .. 与绝对路径是路径不合法,不是「不存在」;文件夹外的文件不会被读出来
        foreach (var path in new[] { "../secret.txt", @"..\secret.txt", "a/../../secret.txt", "/secret.txt", Path.Combine(env.Root, "secret.txt"), "", "a/", "a//b.pdf" })
        {
            Assert.Equal(ErrorCode.FilePathInvalidPath, (await Rejected(service.OpenReadAsync(code, path))).Code);
            Assert.Equal(ErrorCode.FilePathInvalidPath, (await Rejected(service.GetFullPathAsync(code, path))).Code);
        }
    }

    [Fact]
    public async Task Full_path_is_inside_folder()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        var folder = Path.Combine(env.Root, "Docs");
        var service = env.Service<IFilePathDownloadService>();

        var full = await service.GetFullPathAsync(code, @"2026\10\a.pdf");

        Assert.True(FilePathRules.IsUnder(full, folder));
        Assert.Equal(Path.GetFullPath(Path.Combine(folder, "2026", "10", "a.pdf")), full);
    }

    [Fact]
    public async Task Missing_file_directory_or_folder_is_file_not_found()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        await PutAsync(env, "2026/10/a.pdf", "在");
        var service = env.Service<IFilePathDownloadService>();

        // 路径指向目录也不是文件
        foreach (var path in new[] { "2026/10/none.pdf", "no-such-dir/none.pdf", "2026/10" })
            Assert.Equal(ErrorCode.FilePathFileNotFound, (await Rejected(service.OpenReadAsync(code, path))).Code);
    }

    [Fact]
    public async Task Access_failure_is_not_reported_as_missing()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        await PutAsync(env, "a.pdf", "还在");

        // 共享断开 / 权限丢失不能报成「文件不存在」:调用方会据此当作文件已经没了
        var offline = await Rejected(new FaultyService(env, attributes: new IOException("共享已断开", unchecked((int)0x80070035)))
            .OpenReadAsync(code, "a.pdf"));
        Assert.Equal(ErrorCode.FilePathFolderUnavailable, offline.Code);
        Assert.Equal(FileAccessFailure.NetworkPathNotFound, offline.Args!["reason"]);

        var denied = await Rejected(new FaultyService(env, open: new UnauthorizedAccessException())
            .OpenReadAsync(code, "a.pdf"));
        Assert.Equal(ErrorCode.FilePathFolderUnavailable, denied.Code);
        Assert.Equal(FileAccessFailure.AccessDenied, denied.Args!["reason"]);
    }

    [Fact]
    public async Task Invalid_name_error_is_file_not_found()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        var invalidName = new IOException("文件名、目录名或卷标语法不正确", unchecked((int)0x8007007B));

        // Windows 对含 * ? < > | " 的名字报 ERROR_INVALID_NAME:这样的文件不可能存在。读属性和打开两步都可能先撞上
        var atAttributes = await Rejected(new FaultyService(env, attributes: invalidName).OpenReadAsync(code, "a*.pdf"));
        var atOpen = await Rejected(new FaultyService(env, open: invalidName).OpenReadAsync(code, "a*.pdf"));

        Assert.Equal(ErrorCode.FilePathFileNotFound, atAttributes.Code);
        Assert.Equal(ErrorCode.FilePathFileNotFound, atOpen.Code);
    }

    [Fact]
    public async Task Disabled_or_missing_code_is_not_found()
    {
        await using var env = FilePathTestEnv.Create();
        var service = env.Service<IFilePathDownloadService>();

        Assert.Equal(ErrorCode.FilePathNotFound, (await Rejected(service.OpenReadAsync("no-such", "a.pdf"))).Code);
        Assert.Equal(ErrorCode.FilePathNotFound, (await Rejected(service.GetFullPathAsync("no-such", "a.pdf"))).Code);
    }

    [Fact]
    public async Task Backend_call_skips_role_check()
    {
        await using var env = FilePathTestEnv.Create();
        var code = NewCode();
        // 下载角色只有 999,而调用方没有任何登录用户:服务本身不判角色
        await env.Service<IFilePathConfigService>().AddAsync(new FilePathAddInput(
            code, "订单附件", await env.AddServerAsync(), "Docs", FilePathNamingRule.YearMonthOriginal, FilePathConflictMode.Reject,
            false, ".pdf", 10, null, [999], true, null));
        await PutAsync(env, "a.pdf", "能读");

        var download = await env.Service<IFilePathDownloadService>().OpenReadAsync(code, "a.pdf");

        Assert.Equal("能读", await ReadAllAsync(download));
    }

    [Fact]
    public async Task Overwrite_while_reading_succeeds()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env, FilePathNamingRule.BusinessPath, FilePathConflictMode.Overwrite);
        var uploader = env.Service<IFilePathUploadService>();
        var downloader = env.Service<IFilePathDownloadService>();
        await uploader.UploadAsync(code, Upload("a.pdf", "旧"));

        // 旧流还开着:读取不带 FileShare.Delete 时,Windows 上 File.Move(overwrite: true) 会一直失败
        var reading = await downloader.OpenReadAsync(code, "a.pdf");
        try
        {
            await uploader.UploadAsync(code, Upload("a.pdf", "新"));
        }
        finally
        {
            await reading.Content.DisposeAsync();
        }

        Assert.Equal("新", await ReadAllAsync(await downloader.OpenReadAsync(code, "a.pdf")));
    }
}
