using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>租户编辑入参(更新用;不含初始管理员字段——那是创建独有的一次性动作)。</summary>
public record TenantInput
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string? ContactName { get; init; }
    public string? ContactPhone { get; init; }
    public DateTime? ExpireTime { get; init; }
    public TenantIsolationMode IsolationMode { get; init; } = TenantIsolationMode.Shared;
    public bool Enabled { get; init; } = true;
    public string? Remark { get; init; }
}

/// <summary>租户创建入参:在编辑字段基础上,额外携带该租户第一个管理员账号的凭据——
/// 新租户必须带着能登录的管理员一起出生,否则平台管理员建完之后没人能进去继续配置。</summary>
public record TenantCreateInput : TenantInput
{
    public string AdminAccount { get; init; } = "";
    public string AdminPassword { get; init; } = "";
}

public record TenantPageInput : PageInputBase
{
    public string? Name { get; init; }
}
