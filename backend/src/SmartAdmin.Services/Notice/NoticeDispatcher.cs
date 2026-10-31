using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="INoticeDispatcher"/> 默认实现:宿主启动时拉起的后台服务,按序消费内存队列,
/// 对每个能发送的外部登录 provider(<see cref="IExternalNoticeSender"/>)解析接收人并发送。
/// <para>必须由宿主启动,不能在请求线程里懒启动,否则请求的 HttpContext 会经 AsyncLocal 流进后台循环。
/// 队列是单消费者:一条消息重试期间后面的消息等待。进程重启时队列里还没发的消息会丢,站内信不受影响。</para>
/// <para>没有任何 provider 能发送时 <see cref="Enqueue"/> 直接返回,没装外部渠道包的应用不付任何代价。</para>
/// </summary>
public class NoticeDispatcher(IServiceScopeFactory scopes, ILogger<NoticeDispatcher> logger, AdminExternalAuthOptions? options = null)
    : BackgroundService, INoticeDispatcher
{
    private const int QueueCapacity = 10000;
    private const int BatchSize = 1000;
    private const int MaxAttempts = 3;
    private const int MaxUrlBytes = 2048;
    private const string NoticePagePath = "/personal/notice";

    private readonly Channel<NoticeDispatchItem> _queue =
        Channel.CreateBounded<NoticeDispatchItem>(new BoundedChannelOptions(QueueCapacity) { SingleReader = true });

    /// <summary>有没有能发送的渠道:0 未探测,1 有,2 没有。</summary>
    private int _hasSender;

    /// <inheritdoc />
    public virtual void Enqueue(NoticeDispatchItem item)
    {
        try
        {
            if (!HasSender()) return;
            if (!_queue.Writer.TryWrite(item))
                logger.LogError("通知 {NoticeId} 的外部推送队列已满({Capacity}),已丢弃", item.NoticeId, QueueCapacity);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "通知 {NoticeId} 加入外部推送队列失败", item.NoticeId);
        }
    }

    /// <summary>
    /// 探测一次并缓存:有类型声明能发送,或有已注册的 provider 实现了发送接口。
    /// 在临时 scope 里解析而不是构造函数注入:消费者可能把 provider 注册成 Scoped,单例捕获会在启动时报错。
    /// 探测失败不缓存、按"有"处理,交给后台循环去判断。
    /// </summary>
    private bool HasSender()
    {
        var known = Volatile.Read(ref _hasSender);
        if (known != 0) return known == 1;
        try
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var has = sp.GetServices<IExternalAuthProviderType>().Any(t => t.SupportsNotice)
                      || sp.GetServices<IExternalAuthProvider>().Any(p => p is IExternalNoticeSender);
            Volatile.Write(ref _hasSender, has ? 1 : 2);
            return has;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "判断是否有外部推送渠道失败,本条通知按有渠道处理");
            return true;
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessAsync(item, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "通知 {NoticeId} 的外部推送处理失败", item.NoticeId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 停机
        }
    }

    private async Task ProcessAsync(NoticeDispatchItem item, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        var external = services.GetRequiredService<ISysUserExternalService>();
        var providers = await services.GetRequiredService<IExternalAuthProviderRegistry>().ListAsync(cancellationToken);

        ExternalNoticeMessage? message = null;
        var messageBuilt = false;
        foreach (var provider in providers)
        {
            if (provider is not IExternalNoticeSender sender) continue;
            try
            {
                if (!await external.IsEnabledAsync(provider.Code) || !await external.IsNotifyEnabledAsync(provider.Code))
                    continue;

                if (!messageBuilt)
                {
                    message = BuildMessage(item);
                    messageBuilt = true;
                }
                if (message is null)
                {
                    logger.LogWarning("通知 {NoticeId} 未推送:没有配置 SmartAdmin:ExternalAuth:CallbackBaseUrl,拼不出消息链接", item.NoticeId);
                    return;
                }

                var subjects = await ResolveSubjectsAsync(services, item, provider.Code, cancellationToken);
                foreach (var batch in subjects.Chunk(BatchSize))
                    await SendWithRetryAsync(item.NoticeId, provider.Code, sender, message, batch, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 一个 provider 出事不影响后面的 provider
                logger.LogError(ex, "通知 {NoticeId} 推送到 {Provider} 失败", item.NoticeId, provider.Code);
            }
        }
    }

    private async Task SendWithRetryAsync(
        long noticeId, string providerCode, IExternalNoticeSender sender,
        ExternalNoticeMessage message, IReadOnlyList<string> subjects, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = await sender.SendAsync(message, subjects, cancellationToken);
                if (result.Rejected.Count > 0)
                    logger.LogWarning(
                        "通知 {NoticeId} 推送到 {Provider}:{Count} 个接收人被对方拒收({Rejected}),请检查应用可见范围和接口许可",
                        noticeId, providerCode, result.Rejected.Count, string.Join(",", result.Rejected.Take(20)));
                return;
            }
            catch (ExternalNoticeException ex) when (ex.Retryable && attempt < MaxAttempts)
            {
                logger.LogWarning(
                    "通知 {NoticeId} 推送到 {Provider} 第 {Attempt} 次失败,稍后重试(对方错误码 {ProviderCode}):{Reason}",
                    noticeId, providerCode, attempt, ex.ProviderCode, ex.Message);
                await Task.Delay(GetRetryDelay(attempt), cancellationToken);
            }
            catch (ExternalNoticeException ex)
            {
                logger.LogError(
                    "通知 {NoticeId} 推送到 {Provider} 失败,共尝试 {Attempt} 次(对方错误码 {ProviderCode}):{Reason}",
                    noticeId, providerCode, attempt, ex.ProviderCode, ex.Message);
                return;
            }
        }
    }

    /// <summary>
    /// 解析接收人:该 provider 下已绑定、且用户启用的外部账号标识,去重。
    /// 绑定表、用户表、用户角色表都继承 <see cref="BaseEntity"/>,不受机构数据范围过滤,软删由全局过滤器排除。
    /// </summary>
    protected virtual async Task<List<string>> ResolveSubjectsAsync(
        IServiceProvider services, NoticeDispatchItem item, string providerCode, CancellationToken cancellationToken)
    {
        var targets = item.TargetIds.ToList();
        var query = services.GetRequiredService<IRepository<SysUserExternal>>().AsQueryable()
            .InnerJoin<SysUser>((e, u) => e.UserId == u.Id)
            .Where((e, u) => e.Provider == providerCode && u.Enabled == true);

        if (item.ReceiverType == ReceiverType.Role)
            return await query
                .InnerJoin<SysUserRole>((e, u, r) => r.UserId == e.UserId)
                .Where((e, u, r) => targets.Contains(r.RoleId))
                .Select((e, u, r) => e.Subject)
                .Distinct()
                .ToListAsync();

        if (item.ReceiverType == ReceiverType.User)
            query = query.Where((e, u) => targets.Contains(e.UserId));

        return await query.Select((e, u) => e.Subject).Distinct().ToListAsync();
    }

    /// <summary>
    /// 把通知转成外部渠道的消息:标题原样;正文是纯文本,空则用标题;
    /// 链接取正文里第一个站内相对链接,没有就落在通知页。没有配置基址时返回 <c>null</c>(跳过发送)。
    /// </summary>
    protected virtual ExternalNoticeMessage? BuildMessage(NoticeDispatchItem item)
    {
        var link = NoticeText.FirstSiteLink(item.Content);
        var url = AbsoluteUrl(link ?? NoticePagePath);
        // 编码后超长的链接渠道会截断成坏链接,宁可落在通知页
        if (link is not null && (url is null || Encoding.UTF8.GetByteCount(url) > MaxUrlBytes))
            url = AbsoluteUrl(NoticePagePath);
        if (url is null) return null;

        var text = NoticeText.ToPlainText(item.Content);
        return new ExternalNoticeMessage(item.NoticeId, item.Title, text.Length > 0 ? text : item.Title, url);
    }

    /// <summary>失败后的重试间隔:第 1 次失败后 5 秒,第 2 次失败后 30 秒。</summary>
    protected virtual TimeSpan GetRetryDelay(int failedAttempts) =>
        failedAttempts <= 1 ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(30);

    /// <summary>
    /// <c>CallbackBaseUrl</c> 加站内相对路径;没配置、拼不出合法地址,或规范化后跳出了基址所在路径时返回 <c>null</c>。
    /// 网关子路径部署下基址带路径(如 <c>https://gw.example.com/app/</c>),而 <c>Uri</c> 会折叠点段,
    /// <c>/../other</c> 与 <c>/%2e%2e/other</c> 都会落到 <c>/other</c>,所以必须在规范化之后再比一次前缀。
    /// </summary>
    private string? AbsoluteUrl(string path)
    {
        var baseUrl = options?.CallbackBaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl + "/", UriKind.Absolute, out var root)
            || !Uri.TryCreate(baseUrl + path, UriKind.Absolute, out var uri))
            return null;
        return uri.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal) ? uri.AbsoluteUri : null;
    }

    /// <inheritdoc />
    public virtual async Task<ExternalNoticeTestView> SendTestAsync(
        string providerCode, long userId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var services = scope.ServiceProvider;
            var provider = await services.GetRequiredService<IExternalAuthProviderRegistry>().FindAsync(providerCode, cancellationToken);
            if (provider is not IExternalNoticeSender sender) return new ExternalNoticeTestView("unsupported");

            var binding = (await services.GetRequiredService<ISysUserExternalService>().ListByUserAsync(userId))
                .FirstOrDefault(x => x.Provider == providerCode);
            if (binding is null) return new ExternalNoticeTestView("notBound");

            var url = AbsoluteUrl(NoticePagePath);
            if (url is null) return new ExternalNoticeTestView("noBaseUrl");

            // 正文带发送时间:渠道的重复消息检查会吞掉短时间内内容相同的第二次测试
            var message = new ExternalNoticeMessage(0, "测试消息", $"这是一条测试消息,发送时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss}。", url);
            var result = await sender.SendAsync(message, [binding.Subject], cancellationToken);
            return result.Rejected.Count > 0
                ? new ExternalNoticeTestView("rejected", null, string.Join(",", result.Rejected))
                : new ExternalNoticeTestView("ok");
        }
        catch (ExternalNoticeException ex)
        {
            return new ExternalNoticeTestView("failed", ex.ProviderCode, ex.Message);
        }
        // 只放过调用方取消:发送方自己的超时也抛取消异常,那是一次失败,要折成状态返回
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            logger.LogError(ex, "向 {Provider} 发送测试消息失败", providerCode);
            return new ExternalNoticeTestView("failed", null, ex.GetType().Name);
        }
    }
}
