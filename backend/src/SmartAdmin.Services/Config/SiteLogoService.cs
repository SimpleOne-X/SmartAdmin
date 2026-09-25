using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// 站点 Logo 上传。和通用上传(<see cref="IFileService"/>)分开,因为约束不同:
/// Logo 只收位图、上限 1 MB、按文件头认类型,且<b>不受</b>「上传限制」里全局白名单的影响——
/// 管理员把 <c>.png</c> 从全局白名单去掉,不该连 Logo 都换不了。
/// </summary>
public interface ISiteLogoService
{
    /// <summary>校验并保存 Logo 图片,返回文件记录(直链由 HTTP 层签发)。</summary>
    Task<FileUploadOutput> UploadAsync(FileUploadInput input);
}

/// <summary><see cref="ISiteLogoService"/> 默认实现:存储与记账和通用上传一致(<c>sys_file</c> 里能看到这张图)。</summary>
public class SiteLogoService(IRepository<SysFile> files, IFileStorage storage, TimeProvider timeProvider) : ISiteLogoService
{
    /// <summary>Logo 单文件上限(字节)。前端裁剪后导出 256×256 PNG,正常只有几十 KB。</summary>
    protected virtual long MaxBytes => 1024 * 1024;

    /// <inheritdoc />
    public virtual async Task<FileUploadOutput> UploadAsync(FileUploadInput input)
    {
        AdminException.ThrowIf(input.Size <= 0, ErrorCode.FileEmpty);
        AdminException.ThrowIf(input.Size > MaxBytes, ErrorCode.FileTooLarge,
            new Dictionary<string, object?> { ["maxSizeMb"] = MaxBytes / (1024 * 1024) });

        // 最多 1 MB,整份读进内存再验文件头,不依赖上游流可回读
        using var buffer = new MemoryStream();
        await input.Content.CopyToAsync(buffer);
        var ext = SniffExtension(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        AdminException.ThrowIf(ext is null, ErrorCode.FileExtNotAllowed,
            new Dictionary<string, object?> { ["ext"] = Path.GetExtension(input.FileName).ToLowerInvariant() });

        // 后缀取自实际内容而不是文件名:/view 端点按后缀决定 Content-Type,改名的文件不能借此换个类型下发
        buffer.Position = 0;
        var storagePath = $"{timeProvider.GetUtcNow():yyyyMMdd}/{Guid.CreateVersion7():N}{ext}";
        await storage.SaveAsync(buffer, storagePath);

        var entity = new SysFile
        {
            OriginalName = Path.ChangeExtension(Path.GetFileName(input.FileName), ext),
            StoragePath = storagePath,
            Extension = ext!,
            ContentType = input.ContentType,
            SizeBytes = buffer.Length,
        };
        await files.InsertAsync(entity);
        return new FileUploadOutput
        {
            Id = entity.Id,
            OriginalName = entity.OriginalName,
            StoragePath = entity.StoragePath,
            SizeBytes = entity.SizeBytes,
        };
    }

    /// <summary>按文件头识别 PNG / JPEG / WEBP,其余(含 SVG、GIF)返回 null。</summary>
    protected internal static string? SniffExtension(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return ".png";
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) return ".jpg";
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return ".webp";
        return null;
    }
}
