using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// 分片上传暂存的清扫后台任务:每小时扫一遍 <see cref="AdminFilePathOptions.StagingPath"/>,
/// 删掉超过 <see cref="AdminFilePathOptions.StagingTtlHours"/> 小时没有新分片的会话目录——客户端关页面、断网、放弃的上传不会自己清。
/// 会话目录的最后写入时间随每个分片的写入刷新,所以它就是「最近一次收到分片的时间」。
/// <para>只动名字是 <see cref="FilePathRules.IsUploadId"/> 的目录,暂存目录里别的东西一概不碰。
/// 多副本共用暂存卷时几个副本可能同时删同一个目录,后到的遇到的 <see cref="DirectoryNotFoundException"/> 当作已经删了。
/// 单个目录删不掉(被占用、没权限)只记日志,继续下一个,下一轮再试。</para>
/// <para>注册成单例并同时托管为后台任务(同 <c>FileGcService</c>),所以可以按类型注入后直接调 <see cref="SweepOnce"/>。</para>
/// </summary>
public class FilePathStagingSweeper(
    AdminFilePathOptions options,
    TimeProvider time,
    ILogger<FilePathStagingSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 先等一个周期再扫:首启时建表、种子还在跑,没必要跟它们抢
        using var timer = new PeriodicTimer(SweepInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var removed = SweepOnce();
                    if (removed > 0) logger.LogInformation("文件路径暂存清扫:清理 {Sessions} 个过期的分片会话。", removed);
                }
                catch (Exception e)
                {
                    // 清扫失败不能把宿主拖垮(比如暂存卷暂时不可达):记警告,下个周期再来
                    logger.LogWarning(e, "文件路径暂存清扫:本轮失败,下个周期重试。");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 宿主停机,正常退出
        }
    }

    /// <summary>扫一遍暂存目录,返回这一遍清掉的会话数。暂存目录不存在返回 0。测试与手动触发直接调它。</summary>
    public virtual int SweepOnce()
    {
        var root = options.StagingPath;
        if (!Directory.Exists(root)) return 0;

        var cutoff = time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(options.StagingTtlHours);
        var removed = 0;
        // 先取成数组再删:边枚举边删目录,枚举器的行为没有保证
        foreach (var dir in Directory.GetDirectories(root))
        {
            if (!FilePathRules.IsUploadId(Path.GetFileName(dir)) || Directory.GetLastWriteTimeUtc(dir) >= cutoff) continue;

            try
            {
                DeleteSession(dir);
                removed++;
            }
            catch (DirectoryNotFoundException)
            {
                // 别的副本先删掉了
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(e, "删除过期的分片会话 {Session} 失败,下一轮再试", Path.GetFileName(dir));
            }
        }

        return removed;
    }

    /// <summary>删掉一个会话目录及其全部内容。目录已不存在抛 <see cref="DirectoryNotFoundException"/>,由调用方当作已删除。</summary>
    protected virtual void DeleteSession(string dir) => Directory.Delete(dir, recursive: true);
}
