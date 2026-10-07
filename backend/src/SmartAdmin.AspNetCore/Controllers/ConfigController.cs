using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.AspNetCore;

/// <summary>
/// 系统配置管理端点。全部 <c>[RolePermission]</c>——超管放行,普通用户需被授予对应路由权限码。
/// <c>value/{key}</c> 走读穿透缓存,供前端/其他调用方按键取配置值。
/// </summary>
[ApiController]
[Route("api/v1/sys/config")]
[Module("Config")]   // 可经 Api:DisabledModules=["Config"] 关闭
public class ConfigController(IConfigService configs, ISecurityPolicyProvider policy) : ControllerBase
{
    /// <summary>分页查询配置</summary>
    [HttpGet("page")]
    [RolePermission]
    public async Task<Result<PagedList<SysConfig>>> Page([FromQuery] ConfigPageInput input) =>
        Result<PagedList<SysConfig>>.Ok(await configs.PageAsync(input));

    /// <summary>配置详情</summary>
    [HttpGet("{id}")]
    [RolePermission]
    public async Task<Result<SysConfig>> Get(long id) =>
        Result<SysConfig>.Ok(await configs.GetAsync(id));

    /// <summary>按配置键取值(读穿透缓存)</summary>
    [HttpGet("value/{key}")]
    [RolePermission]
    public async Task<Result<string?>> GetValue(string key) =>
        Result<string?>.Ok(await configs.GetValueByKeyAsync(key));

    /// <summary>站点信息(匿名可读:登录前/无配置权限的用户取站点标题等展示白名单,不暴露任意配置)</summary>
    [HttpGet("site")]
    [AllowAnonymous]
    public async Task<Result<SiteInfoOutput>> Site() =>
        Result<SiteInfoOutput>.Ok(await configs.GetSiteInfoAsync());

    /// <summary>当前生效的密码策略(任何登录用户可读:改密/建用户页据此展示规则清单,不需配置读权限)</summary>
    [HttpGet("password-policy")]
    [ActiveSession]
    public async Task<Result<PasswordPolicy>> GetPasswordPolicy() =>
        Result<PasswordPolicy>.Ok(await policy.GetPasswordPolicyAsync());

    /// <summary>批量按键回写配置值(分类配置中心结构化表单保存)</summary>
    [HttpPut("batch")]
    [RolePermission]
    [RequireReauth]
    [OperationLog("批量修改配置")]   // 改的是密码策略/登录锁定/会话时长/限流等安全开关,必须留审计
    public async Task<Result<bool>> Batch([FromBody] IReadOnlyCollection<ConfigBatchItem> items)
    {
        await configs.SaveValuesAsync(items);
        return Result<bool>.Ok(true);
    }

    /// <summary>上传站点 Logo(只收 PNG/JPG/WEBP、1 MB 以内,不受全局上传白名单约束),返回带签名直链的文件信息;
    /// 前端拿 <c>viewUrl</c> 写进 <c>sys.site.logo</c> 后随批量保存生效。</summary>
    [HttpPost("logo")]
    [RolePermission]
    [OperationLog("上传站点 Logo")]   // 入参是 IFormFile,脱敏器会记占位串,不影响记录本次操作
    public async Task<Result<FileUploadOutput>> UploadLogo(
        IFormFile file, [FromServices] ISiteLogoService logos, [FromServices] IFileUrlSigner signer)
    {
        await using var stream = file.OpenReadStream();
        var output = await logos.UploadAsync(new FileUploadInput
        {
            Content = stream,
            FileName = file.FileName,
            Size = file.Length,
            ContentType = file.ContentType,
        });
        return Result<FileUploadOutput>.Ok(output with { ViewUrl = signer.BuildUrl(output.Id) });
    }

    /// <summary>新增配置,返回新 Id</summary>
    [HttpPost]
    [RolePermission]
    [RequireReauth]
    [OperationLog("新增配置")]
    public async Task<Result<long>> Add(ConfigInput input) =>
        Result<long>.Ok(await configs.AddAsync(input));

    /// <summary>更新配置(不含配置键)</summary>
    [HttpPut("{id}")]
    [RolePermission]
    [RequireReauth]
    [OperationLog("修改配置")]
    public async Task<Result<bool>> Update(long id, ConfigInput input)
    {
        await configs.UpdateAsync(id, input);
        return Result<bool>.Ok(true);
    }

    /// <summary>软删除配置</summary>
    [HttpDelete("{id}")]
    [RolePermission]
    [RequireReauth]
    [OperationLog("删除配置")]
    public async Task<Result<bool>> Delete(long id)
    {
        await configs.DeleteAsync(id);
        return Result<bool>.Ok(true);
    }
}
