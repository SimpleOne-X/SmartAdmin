using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// 文件路径的纯规则(无 IO、无状态):相对路径校验、边界判断、文件名清洗、后缀解析、共享路径识别。
/// 路径来自前端和上传的原始文件名,都是不可信输入——这里是它们进入文件系统之前的最后一道关。
/// </summary>
public static class FilePathRules
{
    /// <summary>
    /// 清洗后文件名主干(不含后缀)的字符数上限。
    /// 主干 + <c>_</c> + 8 位短 ID + 后缀,在 UTF-8 下要装得进 255 字节(群晖等 NAS 的上限):60 个 3 字节汉字是 180。
    /// </summary>
    public const int MaxStemLength = 60;

    private const int MaxExtensionDigits = 15;

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static AdminException Invalid() => new(ErrorCode.FilePathInvalidPath);

    /// <summary>
    /// 规范化相对路径:<c>\</c> 统一成 <c>/</c>,返回 <c>a/b/c</c> 形式;空串仅在 <paramref name="allowEmpty"/> 时放行,返回 <c>""</c>。
    /// <para>拒绝绝对路径、盘符、<c>:</c>(NTFS 备用数据流)、控制字符、空段、<c>.</c>/<c>..</c> 段,
    /// 以及任何以点或空格结尾的段——Windows 会悄悄吞掉段尾的点和空格,<c>a.pdf.</c> 打开的是 <c>a.pdf</c>,<c>.. </c> 近似 <c>..</c>。</para>
    /// </summary>
    public static string NormalizeRelative(string? path, bool allowEmpty)
    {
        if (string.IsNullOrEmpty(path))
            return allowEmpty ? "" : throw Invalid();

        var p = path.Replace('\\', '/');
        if (p[0] == '/' || p.Contains(':') || p.Any(c => c < 0x20 || c == 0x7F) || Path.IsPathRooted(p))
            throw Invalid();

        // 段尾是点的判断同时挡掉了 "." 和 ".."
        foreach (var segment in p.Split('/'))
            if (segment.Length == 0 || segment[^1] is '.' or ' ')
                throw Invalid();
        return p;
    }

    /// <summary>
    /// 把已规范化的相对路径拼到 <paramref name="folderFullPath"/> 下,返回完全限定路径;
    /// 拼出来不在该文件夹里就抛 <see cref="ErrorCode.FilePathInvalidPath"/>(纵深防御,调用方应先 <see cref="NormalizeRelative"/>)。
    /// </summary>
    public static string Combine(string folderFullPath, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(folderFullPath, relative));
        return IsUnder(full, folderFullPath) ? full : throw Invalid();
    }

    /// <summary>
    /// <paramref name="path"/> 是否等于 <paramref name="root"/> 或在它下面。
    /// 按「根 + 分隔符」前缀判断,<c>files2</c> 不会误匹配 <c>files</c>;Windows 不区分大小写。
    /// </summary>
    public static bool IsUnder(string path, string root)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var baseDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (full.Equals(baseDir, cmp)) return true;
        var prefix = Path.EndsInDirectorySeparator(baseDir) ? baseDir : baseDir + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, cmp);
    }

    /// <summary>
    /// 是否是分片上传会话的 Id:32 位小写十六进制(<c>Guid.ToString("N")</c>)。它会被拼进暂存路径,也是暂存目录里会话目录的名字,
    /// 上传和清扫都先过这一关,别的名字(<c>..</c>、带分隔符的、大写的、多一位少一位的)一律不是会话。
    /// 逐字符判断而不用 <c>^…$</c> 正则:<c>$</c> 会放过末尾的换行。
    /// </summary>
    public static bool IsUploadId(string? value) =>
        value is { Length: 32 } && value.All(char.IsAsciiHexDigitLower);

    /// <summary><paramref name="path"/> 是否在任一根目录之下。</summary>
    public static bool IsUnderAnyRoot(string path, IEnumerable<string> roots) =>
        roots.Any(root => IsUnder(path, root));

    /// <summary>
    /// 文件服务器根路径是否可用:必须是绝对路径(相对路径按进程当前目录解析,结果随启动方式漂移),且在白名单内。
    /// 保存时和读取时(白名单可能已经改小)用的是同一个判断。
    /// </summary>
    public static bool IsAllowedRoot(string rootPath, IEnumerable<string> allowedRoots) =>
        Path.IsPathFullyQualified(rootPath) && IsUnderAnyRoot(rootPath, allowedRoots);

    /// <summary>角色 Id 列表 → 库里存的逗号串:升序、去重;空或 null 存 <c>null</c>。</summary>
    public static string? FormatRoleIds(IEnumerable<long>? roleIds)
    {
        var ids = (roleIds ?? []).Distinct().Order().ToList();
        return ids.Count == 0 ? null : string.Join(',', ids);
    }

    /// <summary>
    /// 库里的逗号串 → 角色 Id 列表,与 <see cref="FormatRoleIds"/> 互逆。空白串、逗号之间的空项都当没有;
    /// 有任何一项是非空白却解析不出 Id(<c>admin</c>、<c>3;5</c>)就返回 <c>false</c>,<paramref name="roleIds"/> 里是解析得出的那些项。
    /// 「空 = 所有登录用户」,所以鉴权用的调用方必须在 <c>false</c> 时拒绝这一行——整串坏掉不能退化成不限角色。
    /// </summary>
    public static bool TryParseRoleIds(string? csv, out IReadOnlyList<long> roleIds)
    {
        var ids = new List<long>();
        var allParsed = true;
        foreach (var token in (csv ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (long.TryParse(token, out var id)) ids.Add(id);
            else allParsed = false;
        }

        roleIds = ids;
        return allParsed;
    }

    /// <summary>
    /// <see cref="TryParseRoleIds"/> 的宽松版:解析不出的项直接丢掉。只给展示用(配置页要能打开损坏的行、重存修复),不能用来鉴权。
    /// </summary>
    public static IReadOnlyList<long> ParseRoleIds(string? csv)
    {
        TryParseRoleIds(csv, out var roleIds);
        return roleIds;
    }

    /// <summary>相对路径中是否有以点开头的段(探针文件、覆盖用的临时文件、系统隐藏文件都是这种,下载一律当不存在)。</summary>
    public static bool HasHiddenSegment(string relative) =>
        relative.Split('/', '\\').Any(segment => segment.StartsWith('.'));

    /// <summary>
    /// 清洗文件名:只取最后一段;非法字符和控制字符换成 <c>_</c>;去掉末尾的点和空格;开头的点换成 <c>_</c>;
    /// 主干截到 <see cref="MaxStemLength"/> 个字符(不切开代理对);保留设备名前面加 <c>_</c>;清洗后为空用 <c>file</c>。
    /// </summary>
    public static string SanitizeFileName(string name)
    {
        var last = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        var cleaned = new string(last.Select(c => c < 0x20 || c == 0x7F || "\\/:*?\"<>|".Contains(c) ? '_' : c).ToArray())
            .TrimEnd('.', ' ');
        if (cleaned.StartsWith('.')) cleaned = "_" + cleaned[1..];

        var dot = cleaned.LastIndexOf('.');
        var stem = dot < 0 ? cleaned : cleaned[..dot];
        var ext = dot < 0 ? "" : cleaned[dot..];
        if (stem.Length > MaxStemLength)
            stem = stem[..(char.IsHighSurrogate(stem[MaxStemLength - 1]) ? MaxStemLength - 1 : MaxStemLength)];
        // 无后缀时截断可能让主干以点或空格收尾
        var result = (stem + ext).TrimEnd('.', ' ');

        // Windows 把 "CON .txt" 也当设备名,取第一个点之前的部分去掉尾随空格再比
        if (ReservedDeviceNames.Contains(result.Split('.')[0].TrimEnd(' '))) result = "_" + result;
        return result.Length == 0 ? "file" : result;
    }

    /// <summary>取清洗后文件名的后缀(小写、含点);没有后缀返回 <c>""</c>。校验的后缀和落盘的后缀因此是同一个。</summary>
    public static string GetExtension(string fileName) =>
        Path.GetExtension(SanitizeFileName(fileName)).ToLowerInvariant();

    /// <summary>
    /// 解析逗号分隔的后缀白名单:去空白、转小写,每项须是 <c>.</c> 加 1–15 位小写字母或数字,否则抛
    /// <see cref="ErrorCode.FilePathExtensionsInvalid"/>。空串返回空表。
    /// </summary>
    public static IReadOnlyList<string> ParseExtensions(string? csv)
    {
        var list = (csv ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.ToLowerInvariant()).ToList();
        if (list.Any(e => !IsValidExtension(e))) throw new AdminException(ErrorCode.FilePathExtensionsInvalid);
        return list;
    }

    private static bool IsValidExtension(string e) =>
        e.Length is > 1 && e.Length <= MaxExtensionDigits + 1 && e[0] == '.'
        && e.Skip(1).All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c));

    /// <summary>
    /// 根路径是否为 <c>\\host\share[\...]</c> 形式的共享路径;是则给出主机名和共享根(<c>\\host\share</c>)。
    /// 设备路径(<c>\\?\</c>、<c>\\.\</c>)、本地路径、只有主机名的都不算。
    /// </summary>
    public static bool TryGetShare(string rootPath, out string host, out string share)
    {
        host = share = "";
        var p = rootPath.Replace('/', '\\');
        if (!p.StartsWith(@"\\")) return false;

        var parts = p[2..].Split('\\');
        if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0 || parts[0] is "." or "?") return false;

        host = parts[0];
        share = $@"\\{parts[0]}\{parts[1]}";
        return true;
    }

    /// <summary>
    /// 下载时给用户看的文件名。没指定就用请求路径的最后一段(<paramref name="actualFileName"/>);指定了就取清洗后的主干,
    /// 后缀始终沿用 <paramref name="actualFileName"/> 的——调用方不能借 <paramref name="requested"/> 把 <c>.pdf</c> 改成 <c>.exe</c> 再交给浏览器。
    /// </summary>
    public static string DownloadName(string? requested, string actualFileName)
    {
        if (string.IsNullOrWhiteSpace(requested)) return actualFileName;
        return Path.GetFileNameWithoutExtension(SanitizeFileName(requested)) + Path.GetExtension(actualFileName);
    }
}
