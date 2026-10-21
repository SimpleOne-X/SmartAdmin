using System.Text;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>
/// 文件路径的纯规则:相对路径校验、文件名清洗、后缀解析、共享路径识别、失败原因归类。
/// 不起宿主;路径一律用临时目录拼,Linux 上同样能跑。
/// </summary>
public class FilePathRulesTests
{
    private static readonly string Tmp = Path.Combine(Path.GetTempPath(), "sa-filepath-rules");

    [Theory]
    [InlineData("../a")] [InlineData("a/../b")] [InlineData("/a")] [InlineData(@"\\nas01\files\a")]
    [InlineData("C:/a")] [InlineData("a:b")] [InlineData("a\u0001b")] [InlineData("a//b")]
    [InlineData("a/./b")] [InlineData("a./b")] [InlineData("a /b")] [InlineData("a/b.")]
    public void NormalizeRelative_rejects(string path) =>
        Assert.Equal(ErrorCode.FilePathInvalidPath,
            Assert.Throws<AdminException>(() => FilePathRules.NormalizeRelative(path, allowEmpty: false)).Code);

    [Theory]
    [InlineData(@"2026\10\a.pdf", "2026/10/a.pdf")]
    [InlineData("Docs/报价.pdf", "Docs/报价.pdf")]
    public void NormalizeRelative_unifies_separators(string input, string expected) =>
        Assert.Equal(expected, FilePathRules.NormalizeRelative(input, allowEmpty: false));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void NormalizeRelative_empty_only_when_allowed(string? path)
    {
        Assert.Equal("", FilePathRules.NormalizeRelative(path, allowEmpty: true));
        Assert.Equal(ErrorCode.FilePathInvalidPath,
            Assert.Throws<AdminException>(() => FilePathRules.NormalizeRelative(path, allowEmpty: false)).Code);
    }

    [Fact]
    public void IsUnder_requires_separator_boundary()
    {
        var root = Path.Combine(Tmp, "files");
        Assert.False(FilePathRules.IsUnder(Path.Combine(Tmp, "files2", "x"), root));
        Assert.True(FilePathRules.IsUnder(Path.Combine(root, "x"), root));
        Assert.True(FilePathRules.IsUnder(root, root));
    }

    [Fact]
    public void IsUnderAnyRoot_matches_any_listed_root()
    {
        var a = Path.Combine(Tmp, "a");
        var b = Path.Combine(Tmp, "b");
        Assert.True(FilePathRules.IsUnderAnyRoot(Path.Combine(b, "x"), [a, b]));
        Assert.False(FilePathRules.IsUnderAnyRoot(Path.Combine(Tmp, "c", "x"), [a, b]));
    }

    [Fact]
    public void Combine_stays_inside_folder()
    {
        var root = Path.Combine(Tmp, "files");
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "a", "b.pdf")), FilePathRules.Combine(root, "a/b.pdf"));
    }

    [Fact]
    public void Combine_rejects_escape()
    {
        var root = Path.Combine(Tmp, "files");
        Assert.Equal(ErrorCode.FilePathInvalidPath,
            Assert.Throws<AdminException>(() => FilePathRules.Combine(root, "../files2/x")).Code);
    }

    [Theory]
    [InlineData("a:b*c?.pdf", "a_b_c_.pdf")]
    [InlineData(".env", "_env")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("com1", "_com1")]
    [InlineData("COM¹.txt", "_COM¹.txt")]   // Windows 把上标 ¹²³ 也当数字,COM¹ 与 COM1 同为设备名
    [InlineData("lpt³", "_lpt³")]
    [InlineData("LPT² .log", "_LPT² .log")]
    [InlineData("报价单. ", "报价单")]
    [InlineData("", "file")]
    [InlineData("...", "file")]
    [InlineData(@"C:\fakepath\a.pdf", "a.pdf")]
    public void SanitizeFileName_cases(string input, string expected) =>
        Assert.Equal(expected, FilePathRules.SanitizeFileName(input));

    [Theory]
    [InlineData("CON", true)]
    [InlineData("aux.txt", true)]
    [InlineData("NUL .tar.gz", true)]
    [InlineData("COM²", true)]
    [InlineData("LPT¹.pdf", true)]
    [InlineData("CONSOLE", false)]
    [InlineData("COM10", false)]
    [InlineData("_CON.txt", false)]
    public void IsReservedDeviceName_cases(string segment, bool expected) =>
        Assert.Equal(expected, FilePathRules.IsReservedDeviceName(segment));

    [Fact]
    public void SanitizeFileName_truncates_stem_within_255_bytes()
    {
        var result = FilePathRules.SanitizeFileName(new string('报', 100) + ".pdf");
        var stem = Path.GetFileNameWithoutExtension(result);

        Assert.Equal(FilePathRules.MaxStemLength, stem.Length);
        Assert.True(Encoding.UTF8.GetByteCount(stem + "_0123abcd" + ".pdf") <= 255);
    }

    [Fact]
    public void SanitizeFileName_does_not_split_surrogate_pair()
    {
        var result = FilePathRules.SanitizeFileName(new string('a', 59) + "\U0001F600" + "b.png");

        Assert.DoesNotContain(result.EnumerateRunes(), r => r == Rune.ReplacementChar);
        Assert.True(Path.GetFileNameWithoutExtension(result).Length <= FilePathRules.MaxStemLength);
    }

    [Theory]
    [InlineData("a/.probe", true)]
    [InlineData(".x/a.pdf", true)]
    [InlineData("a/b.pdf", false)]
    public void HasHiddenSegment_cases(string relative, bool expected) =>
        Assert.Equal(expected, FilePathRules.HasHiddenSegment(relative));

    [Theory]
    [InlineData("报价.PDF", ".pdf")]
    [InlineData("README", "")]
    [InlineData("a.tar.gz", ".gz")]
    public void GetExtension_cases(string fileName, string expected) =>
        Assert.Equal(expected, FilePathRules.GetExtension(fileName));

    [Fact]
    public void ParseExtensions_trims_and_lowercases() =>
        Assert.Equal([".pdf", ".png"], FilePathRules.ParseExtensions(" .PDF, .png "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ParseExtensions_blank_is_empty(string? csv) =>
        Assert.Empty(FilePathRules.ParseExtensions(csv));

    [Theory]
    [InlineData("pdf")]
    [InlineData(".p df")]
    [InlineData("../x")]
    [InlineData(".abcdefghijklmnop")]
    public void ParseExtensions_rejects_bad_format(string csv) =>
        Assert.Equal(ErrorCode.FilePathExtensionsInvalid,
            Assert.Throws<AdminException>(() => FilePathRules.ParseExtensions(csv)).Code);

    [Theory]
    [InlineData(@"\\nas01\files", "nas01", @"\\nas01\files")]
    [InlineData(@"\\nas01\files\sub", "nas01", @"\\nas01\files")]
    public void TryGetShare_accepts_unc(string root, string host, string share)
    {
        Assert.True(FilePathRules.TryGetShare(root, out var h, out var s));
        Assert.Equal(host, h);
        Assert.Equal(share, s);
    }

    [Theory]
    [InlineData(@"\\?\UNC\nas01\files")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData("/mnt/nas")]
    [InlineData(@"\\nas01")]
    public void TryGetShare_rejects_non_share(string root) =>
        Assert.False(FilePathRules.TryGetShare(root, out _, out _));

    [Theory]
    [InlineData(null, "a_1f2e3d4c.pdf", "a_1f2e3d4c.pdf")]
    [InlineData("报价单.exe", "x.pdf", "报价单.pdf")]
    [InlineData("../../etc/passwd", "x.pdf", "passwd.pdf")]
    [InlineData("a\r\nb", "x.pdf", "a__b.pdf")]
    public void DownloadName_cases(string? requested, string actual, string expected) =>
        Assert.Equal(expected, FilePathRules.DownloadName(requested, actual));

    [Theory]
    [InlineData(5, FileAccessFailure.AccessDenied)]
    [InlineData(53, FileAccessFailure.NetworkPathNotFound)]
    [InlineData(51, FileAccessFailure.NetworkPathNotFound)]     // ERROR_REM_NOT_LIST
    [InlineData(59, FileAccessFailure.NetworkPathNotFound)]     // ERROR_UNEXP_NET_ERR
    [InlineData(64, FileAccessFailure.NetworkPathNotFound)]     // ERROR_NETNAME_DELETED:已经建立的会话断了
    [InlineData(1203, FileAccessFailure.NetworkPathNotFound)]   // ERROR_NO_NET_OR_BAD_PATH
    [InlineData(1222, FileAccessFailure.NetworkPathNotFound)]   // ERROR_NO_NETWORK
    [InlineData(1231, FileAccessFailure.NetworkPathNotFound)]   // ERROR_NETWORK_UNREACHABLE
    [InlineData(1232, FileAccessFailure.NetworkPathNotFound)]   // ERROR_HOST_UNREACHABLE
    [InlineData(121, FileAccessFailure.Timeout)]                // ERROR_SEM_TIMEOUT
    [InlineData(67, FileAccessFailure.ShareNotFound)]
    [InlineData(1326, FileAccessFailure.BadCredentials)]
    [InlineData(86, FileAccessFailure.BadCredentials)]
    [InlineData(1330, FileAccessFailure.BadCredentials)]
    [InlineData(1909, FileAccessFailure.AccountLocked)]
    [InlineData(1331, FileAccessFailure.AccountLocked)]
    [InlineData(1219, FileAccessFailure.CredentialConflict)]
    [InlineData(9999, FileAccessFailure.Unknown)]
    public void FromWin32_cases(int code, string expected) =>
        Assert.Equal(expected, FileAccessFailure.FromWin32(code));

    [Fact]
    public void FromException_cases()
    {
        Assert.Equal(FileAccessFailure.AccessDenied, FileAccessFailure.FromException(new UnauthorizedAccessException()));
        Assert.Equal(FileAccessFailure.FolderMissing, FileAccessFailure.FromException(new DirectoryNotFoundException()));
        Assert.Equal(FileAccessFailure.Timeout, FileAccessFailure.FromException(new TimeoutException()));
        Assert.Equal(FileAccessFailure.NetworkPathNotFound,
            FileAccessFailure.FromException(new IOException("", unchecked((int)0x80070035))));
        Assert.Equal(FileAccessFailure.BadCredentials,
            FileAccessFailure.FromException(new FileServerConnectException(FileAccessFailure.BadCredentials, 1326)));
        Assert.Equal(FileAccessFailure.Unknown, FileAccessFailure.FromException(new IOException("x")));
        Assert.Equal(FileAccessFailure.Unknown, FileAccessFailure.FromException(new InvalidOperationException()));
        // 熔断、繁忙抛的「文件夹无法访问」带着原因,探针要原样报出来,不能变成「未知」
        Assert.Equal(FileAccessFailure.Busy, FileAccessFailure.FromException(
            new AdminException(ErrorCode.FilePathFolderUnavailable, new Dictionary<string, object?> { ["reason"] = FileAccessFailure.Busy })));
    }

    [Fact]
    public void ToError_carries_reason_arg()
    {
        var ex = FileAccessFailure.ToError(new UnauthorizedAccessException());

        Assert.Equal(ErrorCode.FilePathFolderUnavailable, ex.Code);
        Assert.Equal(FileAccessFailure.AccessDenied, ex.Args!["reason"]);
    }

    [Fact]
    public void RoleIds_format_sorted_distinct_and_parse_back()
    {
        Assert.Equal("3,5", FilePathRules.FormatRoleIds([5, 3, 5]));
        Assert.Null(FilePathRules.FormatRoleIds([]));
        Assert.Null(FilePathRules.FormatRoleIds(null));

        Assert.Equal([3L, 5L], FilePathRules.ParseRoleIds("3,5"));
        Assert.Equal([3L, 5L], FilePathRules.ParseRoleIds(" 3 , x,,5 "));
        Assert.Empty(FilePathRules.ParseRoleIds(null));
        Assert.Empty(FilePathRules.ParseRoleIds(""));
    }

    [Fact]
    public void IsAllowedRoot_requires_absolute_path_inside_whitelist()
    {
        var root = Path.Combine(Tmp, "files");
        var options = new AdminFilePathOptions { AllowedRoots = [root] };

        Assert.True(FilePathRules.IsAllowedRoot(root, options));
        Assert.True(FilePathRules.IsAllowedRoot(Path.Combine(root, "sub"), options));
        Assert.False(FilePathRules.IsAllowedRoot(root + "2", options));
        Assert.False(FilePathRules.IsAllowedRoot(Path.Combine("files", "sub"),
            new AdminFilePathOptions { AllowedRoots = [Path.GetFullPath("files")] }));
        Assert.Equal(ErrorCode.FileServerRootNotAllowed, FilePathRules.RootRejection(options));
    }

    [Fact]
    public void IsAllowedRoot_without_whitelist_keeps_clear_of_program_and_staging_directories()
    {
        var staging = Path.Combine(Tmp, "staging");
        var options = new AdminFilePathOptions { StagingPath = staging };
        var program = AppContext.BaseDirectory;

        Assert.True(FilePathRules.IsAllowedRoot(Path.Combine(Tmp, "files"), options));
        Assert.True(FilePathRules.IsAllowedRoot(staging + "2", options));
        // 等于、落在里面、包含它们都不行
        foreach (var root in new[]
                 {
                     program, Path.Combine(program, "wwwroot"), Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(program))!,
                     Path.GetPathRoot(program)!, staging, Path.Combine(staging, "x"), Tmp,
                 })
            Assert.False(FilePathRules.IsAllowedRoot(root, options), root);
        Assert.False(FilePathRules.IsAllowedRoot(Path.Combine("files", "sub"), options));
        Assert.Equal(ErrorCode.FileServerRootProtected, FilePathRules.RootRejection(options));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("  ")] [InlineData(" , ,")]
    public void TryParseRoleIds_blank_means_no_restriction(string? csv)
    {
        Assert.True(FilePathRules.TryParseRoleIds(csv, out var ids));
        Assert.Empty(ids);
    }

    [Theory]
    [InlineData("3,5", true, new[] { 3L, 5L })]
    [InlineData(",3,,5, ", true, new[] { 3L, 5L })]
    [InlineData("admin", false, new long[0])]
    [InlineData("3;5", false, new long[0])]
    [InlineData(" 3 , x,,5 ", false, new[] { 3L, 5L })]
    public void TryParseRoleIds_flags_unparseable_tokens(string csv, bool expectedOk, long[] expectedIds)
    {
        Assert.Equal(expectedOk, FilePathRules.TryParseRoleIds(csv, out var ids));
        Assert.Equal(expectedIds, ids);
        // 宽松版只是丢掉坏项,不改变能解析出的部分
        Assert.Equal(expectedIds, FilePathRules.ParseRoleIds(csv));
    }
}
