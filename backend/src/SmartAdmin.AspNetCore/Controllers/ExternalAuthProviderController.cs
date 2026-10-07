using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.AspNetCore;

/// <summary>
/// 第三方登录连接配置的管理端点(系统配置 → 登录方式)。<c>[Module("ExternalAuth")]</c> 与登录端点同属一个模块,可整段禁用。
/// <para>能改这里就能决定谁能登录系统,所以写操作都挂 <c>[RequireReauth]</c>;连接测试不落库,只要 <c>[RolePermission]</c>。
/// 响应里没有任何机密明文:机密字段只回「是否已配置 + 尾四位」。</para>
/// <para>请求体里的机密字典属性必须叫 <c>Secrets</c>:操作日志脱敏器按名字子串匹配 <c>secret</c>,整包打码。
/// 写操作与测试的动作不声明 <c>CancellationToken</c> 参数,取消信号用 <c>HttpContext.RequestAborted</c>:
/// 操作日志把动作入参整体序列化,<c>CancellationToken</c> 序列化不了会让整条入参记成占位串。</para>
/// </summary>
[ApiController]
[Route("api/v1/sys/external-auth/providers")]
[Module("ExternalAuth")]
public class ExternalAuthProviderController(
    IExternalAuthProviderService service,
    AdminExternalAuthOptions options,
    IWebHostEnvironment env) : ControllerBase
{
    /// <summary>回调地址模板里代表「标识」的占位;新增 OIDC 时标识还没定,前端用它拼出预览</summary>
    public const string CallbackCodePlaceholder = "{code}";

    /// <summary>
    /// 目录:已装的类型、全部 provider(配置状态、非机密字段值、机密字段的 hasValue 与尾四位、要填到厂商后台的回调地址),
    /// 以及 <c>dataProtectionEphemeral</c>(主密钥是临时的,保存会被拒)与 <c>callbackBaseUrlMissing</c>(生产环境没配回调基址)两个页面级告警。
    /// </summary>
    [HttpGet]
    [RolePermission]
    public async Task<Result<ExternalAuthCatalogOutput>> Get(CancellationToken cancellationToken)
    {
        var catalog = await service.GetCatalogAsync(cancellationToken);
        var missing = ExternalAuthCallback.BaseUrlMissing(options, env);
        var providers = catalog.Providers
            .Select(p => p with { CallbackUri = missing ? "" : ExternalAuthCallback.BuildUri(options, env, Request, p.Code) })
            .ToList();
        return Result<ExternalAuthCatalogOutput>.Ok(new ExternalAuthCatalogOutput(
            catalog.DataProtectionEphemeral, missing, catalog.Types, providers,
            missing ? "" : ExternalAuthCallback.BuildUri(options, env, Request, CallbackCodePlaceholder)));
    }

    /// <summary>新增或更新一条配置(body 带 <c>type</c>)。机密字段留空 = 不修改,首次保存必填;改了 OIDC 的 Authority 要重输机密。</summary>
    [HttpPut("{code}")]
    [RolePermission]
    [RequireReauth]
    [OperationLog("保存登录方式配置")]
    public async Task<Result<bool>> Save(string code, ExternalAuthProviderSaveInput input)
    {
        await service.SaveAsync(code, input, HttpContext.RequestAborted);
        return Result<bool>.Ok(true);
    }

    /// <summary>清除配置(软删)。不会删除用户已绑定的外部账号。</summary>
    [HttpDelete("{code}")]
    [RolePermission]
    [RequireReauth]
    [OperationLog("清除登录方式配置")]
    public async Task<Result<bool>> Delete(string code)
    {
        await service.DeleteAsync(code, HttpContext.RequestAborted);
        return Result<bool>.Ok(true);
    }

    /// <summary>连接测试:用表单里的值测,机密留空则用已保存的;不落库。结果逐项列出,失败原因在返回体里,不抛异常。</summary>
    [HttpPost("test")]
    [RolePermission]
    [OperationLog("测试登录方式连接")]
    public async Task<Result<ExternalAuthTestView>> Test(ExternalAuthProviderTestInput input) =>
        Result<ExternalAuthTestView>.Ok(await service.TestAsync(input, HttpContext.RequestAborted));
}

/// <summary>「登录方式」目录出参:在服务层目录之上加上页面级的回调基址告警。</summary>
/// <param name="DataProtectionEphemeral">数据保护主密钥是进程内临时密钥,此时保存会被拒(40034)</param>
/// <param name="CallbackBaseUrlMissing">生产环境没配 <c>CallbackBaseUrl</c>,登录时控制器会抛异常</param>
/// <param name="Types">已安装的类型</param>
/// <param name="Providers">全部 provider;每项带回调地址</param>
/// <param name="CallbackUriTemplate">回调地址模板,标识处为 <c>{code}</c>;没配回调基址时为空串</param>
public sealed record ExternalAuthCatalogOutput(
    bool DataProtectionEphemeral,
    bool CallbackBaseUrlMissing,
    IReadOnlyList<ExternalAuthTypeView> Types,
    IReadOnlyList<ExternalAuthProviderView> Providers,
    string CallbackUriTemplate);
