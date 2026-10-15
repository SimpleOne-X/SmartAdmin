using Microsoft.AspNetCore.Mvc;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.AspNetCore;

/// <summary>
/// 消息通知端点(轮询模型)。
/// <para>管理端(发布/列表/删除)挂 <c>[RolePermission]</c>——超管放行,普通用户需被授予对应路由权限码;
/// 用户端(我的通知/未读数/标记已读)挂 <c>[ActiveSession]</c>——任何登录用户可用,无需专门权限码。</para>
/// </summary>
[ApiController]
[Route("api/v1/sys/notice")]
[Module("Notice")]   // 可经 Api:DisabledModules=["Notice"] 关闭
public class NoticeController(INoticeService notices) : ControllerBase
{
    // ── 管理端 ──────────────────────────────────────────────────────

    /// <summary>发布通知(广播全体),返回新 Id</summary>
    [HttpPost]
    [RolePermission]
    [OperationLog("发布通知")]
    public async Task<Result<long>> Publish(NoticePublishInput input) =>
        Result<long>.Ok(await notices.PublishAsync(input));

    /// <summary>分页查询全部通知(管理端)</summary>
    [HttpGet("page")]
    [RolePermission]
    public async Task<Result<PagedList<SysNotice>>> Page([FromQuery] NoticePageInput input) =>
        Result<PagedList<SysNotice>>.Ok(await notices.PageAsync(input));

    /// <summary>软删除通知</summary>
    [HttpDelete("{id}")]
    [RolePermission]
    [OperationLog("删除通知")]
    public async Task<Result<bool>> Delete(long id)
    {
        await notices.DeleteAsync(id);
        return Result<bool>.Ok(true);
    }

    // ── 用户端(任何登录用户) ────────────────────────────────────────

    /// <summary>分页查询"我的通知"(含已读标记)</summary>
    [HttpGet("mine")]
    [ActiveSession]
    public async Task<Result<PagedList<NoticeMineItem>>> Mine([FromQuery] NoticePageInput input) =>
        Result<PagedList<NoticeMineItem>>.Ok(await notices.PageMineAsync(input));

    /// <summary>当前用户未读通知数(顶栏角标);带 Types 时只数这几种类型(铃铛页签角标)</summary>
    [HttpGet("unread-count")]
    [ActiveSession]
    public async Task<Result<int>> UnreadCount([FromQuery(Name = "Types")] NoticeType[]? types = null) =>
        // 不带类型时走无参重载:覆写过它的子类在顶栏角标上的行为保持不变。
        Result<int>.Ok(types is { Length: > 0 }
            ? await notices.UnreadCountAsync(types)
            : await notices.UnreadCountAsync());

    /// <summary>标记某条通知为已读(幂等)</summary>
    [HttpPut("{id}/read")]
    [ActiveSession]
    public async Task<Result<bool>> Read(long id)
    {
        await notices.MarkReadAsync(id);
        return Result<bool>.Ok(true);
    }

    /// <summary>标记全部通知为已读;带 Types 时只标这几种类型(铃铛当前页签)</summary>
    [HttpPut("read-all")]
    [ActiveSession]
    public async Task<Result<bool>> ReadAll([FromQuery(Name = "Types")] NoticeType[]? types = null)
    {
        if (types is { Length: > 0 })
            await notices.MarkAllReadAsync(types);
        else
            await notices.MarkAllReadAsync();
        return Result<bool>.Ok(true);
    }
}
