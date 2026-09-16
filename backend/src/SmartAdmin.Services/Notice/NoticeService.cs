using System.Text.Json;
using System.Text.Json.Serialization;
using SqlSugar;
using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="INoticeService"/> 默认实现。通知为全体广播(实体继承 <see cref="BaseEntity"/> 不带机构范围),
/// 每用户已读状态记 <see cref="SysNoticeRead"/>(存在一行=已读)。未读数每次实时统计——
/// <c>// ponytail: 未读用两条 COUNT 级查询实时算,不缓存(避免发布/已读后缓存滞后);用户量巨大再上按用户缓存 + 发布/已读时失效。</c>
/// </summary>
public class NoticeService(
    IRepository<SysNotice> notices,
    IRepository<SysNoticeRead> reads,
    IRepository<SysNoticeReceiver> receivers,
    IRbacService rbac,
    ICurrentUser currentUser,
    IRealtimePublisher? realtime = null,
    // 定向发布前校验接收目标存在性(角色/用户),尾随可选参数,消费者子类省略也能编译;
    // 未注入时(如手工构造的测试实例)跳过校验,不阻断发布。
    IRepository<SysUser>? users = null,
    IRepository<SysRole>? roles = null,
    // 数据范围守卫;可选尾参,未注入时不收敛
    IDataScopeGuard? scopeGuard = null) : INoticeService
{
    /// <summary>当前登录用户 Id(用户端端点均 <c>[ActiveSession]</c> 保证已认证;缺失按令牌失效处理)。</summary>
    private long CurrentUserId
    {
        get
        {
            AdminException.ThrowIf(currentUser.UserId is null, ErrorCode.TokenInvalid);
            return currentUser.UserId!.Value;
        }
    }

    /// <summary>
    /// "当前用户可见"的通知查询:全体广播 + 定向到我角色/我本人的。
    /// <para>ICurrentUser 不带角色,角色经 <see cref="IRbacService.GetUserRoleIdsAsync"/>(已缓存)取;
    /// 定向命中的 NoticeId 用一次 join 预载(ponytail: 全量载入,定向通知量巨大再改 exists 子查询)。</para>
    /// </summary>
    protected virtual async Task<ISugarQueryable<SysNotice>> VisibleToMeAsync(long me)
    {
        var myRoleIds = (await rbac.GetUserRoleIdsAsync(me)).ToList();
        var targetedIds = await receivers.AsQueryable()
            .InnerJoin<SysNotice>((r, n) => r.NoticeId == n.Id)
            .Where((r, n) => (n.ReceiverType == ReceiverType.Role && myRoleIds.Contains(r.ReceiverId))
                          || (n.ReceiverType == ReceiverType.User && r.ReceiverId == me))
            .Select((r, n) => r.NoticeId)
            .ToListAsync();
        return notices.AsQueryable()
            .Where(n => n.ReceiverType == ReceiverType.All || targetedIds.Contains(n.Id));
    }

    /// <inheritdoc />
    public virtual async Task<long> PublishAsync(NoticePublishInput input)
    {
        // 定向发布(非全体广播)必须先过关:目标非空 + 全部真实存在(启用中、未删除)。
        // 在插入通知本体<b>之前</b>校验并整体拒绝——避免插了通知却一个接收目标都没落地(或落了假目标)的半成品状态。
        List<long> targetIds = [];
        if (input.ReceiverType != ReceiverType.All)
        {
            targetIds = (input.ReceiverIds ?? []).Distinct().ToList();
            AdminException.ThrowIf(targetIds.Count == 0, ErrorCode.NoticeReceiverRequired);
            await EnsureReceiversExistAsync(input.ReceiverType, targetIds);
        }

        var entity = new SysNotice
        {
            Title = input.Title,
            Content = input.Content,
            Type = input.Type,
            ReceiverType = input.ReceiverType,
            ActionsJson = SerializeActions(input.Actions),
            // 显式定死租户归属,不指望插入 AOP:AOP 只在 currentUser.TenantId 有值时才回填,
            // 而这里的调用方也可能是系统上下文(JobExecutor 的 Panic 告警、无 HttpContext 的后台任务),
            // 那时会留下一行 TenantId=null 的通知——在租户过滤器眼里谁都看不见,要等下次启动
            // TenantBackfillHook 回填才冒出来。兜底目标与该钩子的回填目标取同一个值,不是另立一套策略。
            TenantId = currentUser.TenantId ?? DefaultTenantSeed.DEFAULT_TENANT_ID,
        };
        await notices.InsertAsync(entity);
        // 定向发送:每个目标(角色 Id 或用户 Id)写一行接收目标;全体广播不写行。
        if (targetIds.Count > 0)
        {
            // 每一行都要带上与通知本体<b>同一个</b>租户号,理由比通知本体那处更硬:
            // VisibleToMeAsync 是先查 SysNoticeReceiver 拿到 targetedIds、再据此筛通知。接收目标行留 null 时,
            // 收件人以自己的租户身份登录后这一步就查不到行 → targetedIds 为空 → 定向通知在"我的通知"里根本不出现。
            // 净效果是"通知在库里、收件人看不到",等于白发——这正是系统上下文发 Panic 告警的那条路径。
            var tenantId = currentUser.TenantId ?? DefaultTenantSeed.DEFAULT_TENANT_ID;
            var rows = targetIds
                .Select(rid => new SysNoticeReceiver { NoticeId = entity.Id, ReceiverId = rid, TenantId = tenantId })
                .ToList();
            await receivers.InsertRangeAsync(rows);
        }
        // 实时推送(开启时):发布即广播 notice-changed,各端立刻自查未读角标(替代最长 30s 轮询延迟)。
        // ponytail: 广播让各端自查(client 再打 unread-count,2 条 COUNT 很便宜);定向精确唤醒 receiver(角色→用户)集,公告量大再优化。
        if (realtime is not null)
            await realtime.NotifyAllAsync("notice-changed");
        return entity.Id;
    }

    /// <summary>
    /// 校验定向发布的接收目标全部真实存在(保守语义:启用中 + 未删除——全局软删过滤器已排除已删行)。
    /// 未注入对应仓储(消费者精简子类)时跳过校验,不阻断发布。
    /// </summary>
    protected virtual async Task EnsureReceiversExistAsync(ReceiverType type, IReadOnlyCollection<long> targetIds)
    {
        if (type == ReceiverType.Role)
        {
            if (roles is null) return;
            var existing = await ReceiverExistenceQuery(roles.AsQueryable())
                .Where(r => targetIds.Contains(r.Id) && r.Enabled)
                .Select(r => r.Id).ToListAsync();
            AdminException.ThrowIf(targetIds.Except(existing).Any(), ErrorCode.NoticeReceiverNotFound);
        }
        else if (type == ReceiverType.User)
        {
            if (users is null) return;
            var existing = await ReceiverExistenceQuery(users.AsQueryable())
                .Where(u => targetIds.Contains(u.Id) && u.Enabled)
                .Select(u => u.Id).ToListAsync();
            AdminException.ThrowIf(targetIds.Except(existing).Any(), ErrorCode.NoticeReceiverNotFound);
        }
    }

    /// <summary>
    /// 接收目标存在性校验专用的查询:<b>仅</b>在没有租户上下文时清租户过滤器,有上下文时原样返回。
    /// <para>不清的后果不是"查得少"而是丢数据:<c>ITenantScoped</c> 全局过滤器对
    /// <c>currentUser.TenantId</c> 为 null 的调用者<b>恒零行</b>(见 <c>SqlSugarSetup.AttachHooks</c>),
    /// 于是 <c>JobExecutor</c> 的 Panic 告警在系统上下文里把"超管确实存在"也判成"目标不存在"、
    /// 抛 <see cref="ErrorCode.NoticeReceiverNotFound"/>,被上游 try/catch 吞成一条警告——
    /// 而这一步在插入通知<b>之前</b>,那条 INSERT 根本没跑到。同款写法见
    /// <c>SqlSugarRepository.ReleaseUniqueColumnsAsync</c>:识别身份发生在租户上下文确立之前。</para>
    /// <para>为什么不无条件清:那会开一个跨租户 IDOR。已认证的租户 A 管理员拿租户 B 的用户 Id 当定向目标时,
    /// 这层校验是唯一的把关处(后续插入不再按目标查库),放行就等于既能给别的租户的人发通知,
    /// 又能拿错误码当探针问出"B 租户有没有这个 Id"。有租户上下文时,"看不见"就得继续判成"不存在"。</para>
    /// <para>用带类型参数的 <c>ClearFilter&lt;ITenantScoped&gt;()</c> 而非无参 <c>ClearFilter()</c>:
    /// 后者会把消费者自挂的其它过滤器一并跳过,这里只想越过租户这一层(同 <c>SqlSugarRepository</c> 的取舍)。</para>
    /// </summary>
    protected virtual ISugarQueryable<T> ReceiverExistenceQuery<T>(ISugarQueryable<T> query)
        where T : ITenantScoped =>
        currentUser.TenantId is null ? query.ClearFilter<ITenantScoped>() : query;

    /// <inheritdoc />
    public virtual async Task<PagedList<SysNotice>> PageAsync(NoticePageInput input)
    {
        // 管理端列表按发布人所在范围收敛:非超管只看得见自己范围内的人发过什么公告。
        // 用户端的"我的通知"不受此影响——那条走的是投递目标,与谁发的无关。
        var scopedIds = scopeGuard is null ? null : await scopeGuard.ResolveScopedUserIdsAsync();
        return await notices.AsQueryable()
            .WhereIF(scopedIds != null, n => n.CreateUserId != null && scopedIds!.Contains(n.CreateUserId.Value))
            .WhereIF(!string.IsNullOrEmpty(input.Title), n => n.Title.Contains(input.Title!))
            .WhereIF(input.Type.HasValue, n => n.Type == input.Type!.Value)
            .OrderBy(n => n.CreateTime, OrderByType.Desc)
            .ToPagedListAsync(input.Current, input.Size);
    }

    /// <inheritdoc />
    public virtual async Task DeleteAsync(long id)
    {
        var notice = await notices.GetByIdAsync(id);
        AdminException.ThrowIf(notice is null, ErrorCode.NoticeNotFound);
        await notices.DeleteAsync(id);
        // 已读回执行为无害孤儿,不清理:未读统计只看"活通知里没读过的",删掉的通知自然不计。
    }

    /// <inheritdoc />
    public virtual async Task<PagedList<NoticeMineItem>> PageMineAsync(NoticePageInput input)
    {
        var me = CurrentUserId;
        // 已读回执全量载入,既用于 OnlyUnread 过滤、也用于回填 IsRead。ponytail: 量大再改子查询。
        var readIds = await reads.AsQueryable().Where(r => r.UserId == me).Select(r => r.NoticeId).ToListAsync();
        var visible = await VisibleToMeAsync(me);
        var page = await visible
            .WhereIF(!string.IsNullOrEmpty(input.Title), n => n.Title.Contains(input.Title!))
            .WhereIF(input.Type.HasValue, n => n.Type == input.Type!.Value)
            .WhereIF(input.OnlyUnread == true && readIds.Count > 0, n => !readIds.Contains(n.Id))
            .OrderBy(n => n.CreateTime, OrderByType.Desc)
            .ToPagedListAsync(input.Current, input.Size);

        var readSet = readIds.ToHashSet();
        return new PagedList<NoticeMineItem>
        {
            Current = page.Current,
            Size = page.Size,
            Total = page.Total,
            Items = page.Items.Select(n => new NoticeMineItem
            {
                Id = n.Id,
                Title = n.Title,
                Content = n.Content,
                Type = n.Type,
                PublishTime = n.CreateTime,
                IsRead = readSet.Contains(n.Id),
                Actions = DeserializeActions(n.ActionsJson),
            }).ToList(),
        };
    }

    // ── 动作列表 ──────────────────────────────────────────────────────

    /// <summary>一条通知最多带几个动作(按钮区放不下更多,也没有更多的合理用法)。</summary>
    public const int MaxActions = 5;

    /// <summary>与 <see cref="SysNotice.ActionsJson"/> 的列宽一致。</summary>
    public const int MaxActionsJsonLength = 1000;

    private static readonly JsonSerializerOptions ActionsJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,   // null 与 false 不落盘,列宽 1000 省着用
    };

    /// <summary>
    /// 校验并序列化动作列表;空列表存 null。方法名归一成大写,文案与 url 去首尾空白。
    /// 不合法的条目抛 <see cref="ErrorCode.NoticeActionInvalid"/>,args 带出错的下标 index。
    /// </summary>
    protected virtual string? SerializeActions(IReadOnlyList<NoticeAction>? actions)
    {
        if (actions is null || actions.Count == 0) return null;
        AdminException.ThrowIf(actions.Count > MaxActions, ErrorCode.NoticeActionInvalid,
            new Dictionary<string, object?> { ["index"] = MaxActions });

        var normalized = new List<NoticeAction>(actions.Count);
        for (var i = 0; i < actions.Count; i++)
        {
            var a = actions[i];
            var method = (a.Method ?? "").Trim().ToUpperInvariant();
            var label = (a.Label ?? "").Trim();
            var url = (a.Url ?? "").Trim();
            var style = string.IsNullOrWhiteSpace(a.Style) ? null : a.Style.Trim();
            var ok = label.Length > 0
                && method is "GET" or "POST"
                && IsSiteRelativeUrl(url)
                && style is null or "primary" or "default" or "error";
            AdminException.ThrowIf(!ok, ErrorCode.NoticeActionInvalid,
                new Dictionary<string, object?> { ["index"] = i });
            normalized.Add(a with { Label = label, Method = method, Url = url, Style = style });
        }

        var json = JsonSerializer.Serialize(normalized, ActionsJsonOptions);
        AdminException.ThrowIf(json.Length > MaxActionsJsonLength, ErrorCode.NoticeActionInvalid,
            new Dictionary<string, object?> { ["index"] = -1 });
        return json;
    }

    /// <summary>
    /// 只接受以<b>单个</b> <c>/</c> 开头的站内相对路径。<c>//host</c> 与 <c>/\host</c> 是协议相对 URL,
    /// 浏览器会跳出站;控制字符与空白也拒绝,免得存进去的 url 在前端拼不出请求。
    /// </summary>
    protected virtual bool IsSiteRelativeUrl(string url)
    {
        if (url.Length < 1 || url[0] != '/') return false;
        if (url.Length > 1 && url[1] is '/' or '\\') return false;
        foreach (var ch in url)
            if (char.IsWhiteSpace(ch) || char.IsControl(ch) || ch == '\\') return false;
        return true;
    }

    /// <summary>反序列化动作列表;空或坏 JSON 一律读作"没有动作",不让一行脏数据拖垮整页列表。</summary>
    protected virtual IReadOnlyList<NoticeAction>? DeserializeActions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var list = JsonSerializer.Deserialize<List<NoticeAction>>(json, ActionsJsonOptions);
            return list is { Count: > 0 } ? list : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public virtual async Task<int> UnreadCountAsync()
    {
        var me = CurrentUserId;
        // 未读 = 可见通知里当前用户没有已读回执的。ponytail: readIds 全量载入,量大再改 NotExists 子查询。
        var readIds = await reads.AsQueryable().Where(r => r.UserId == me).Select(r => r.NoticeId).ToListAsync();
        var visible = await VisibleToMeAsync(me);
        return await visible
            .WhereIF(readIds.Count > 0, n => !readIds.Contains(n.Id))
            .CountAsync();
    }

    /// <inheritdoc />
    public virtual async Task MarkReadAsync(long noticeId)
    {
        var me = CurrentUserId;
        // 必须先确认这条对当前用户可见:否则任何人可对任意 Id 写已读回执(污染表 / 日后被定向到自己时已是「已读」)。
        var visible = await VisibleToMeAsync(me);
        AdminException.ThrowIf(
            !await visible.AnyAsync(n => n.Id == noticeId),
            ErrorCode.NoticeNotFound);
        var already = await reads.AsQueryable().AnyAsync(r => r.UserId == me && r.NoticeId == noticeId);
        if (already) return; // 幂等:已读回执唯一,不重复插
        await reads.InsertAsync(new SysNoticeRead { UserId = me, NoticeId = noticeId });
    }

    /// <inheritdoc />
    public virtual async Task MarkAllReadAsync()
    {
        var me = CurrentUserId;
        var readIds = await reads.AsQueryable().Where(r => r.UserId == me).Select(r => r.NoticeId).ToListAsync();
        var visible = await VisibleToMeAsync(me);
        var unreadIds = await visible
            .WhereIF(readIds.Count > 0, n => !readIds.Contains(n.Id))
            .Select(n => n.Id).ToListAsync();
        // ponytail: 逐条插入已读回执;未读量巨大再改批量插入。
        foreach (var noticeId in unreadIds)
            await reads.InsertAsync(new SysNoticeRead { UserId = me, NoticeId = noticeId });
    }
}
