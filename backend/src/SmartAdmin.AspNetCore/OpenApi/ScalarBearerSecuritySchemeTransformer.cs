using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace SmartAdmin.AspNetCore;

/// <summary>
/// 声明 Bearer JWT 的 <c>securityScheme</c>,并把它设为每个 operation 的安全要求。
/// <para><b>为什么要</b>:生产环境显式开启后 <c>/openapi/{documentName}.json</c> 被 ScalarAccess 策略网关
/// (见 <see cref="ScalarAccessAuthorizationHandler"/>),管理员要在 Scalar 的 Authentication 面板里粘贴自己的
/// 接口令牌,Scalar 才能带着它去取契约。而那个面板是<b>按契约里声明的 securityScheme 渲染的</b>——
/// 契约里一个方案都不声明,面板就是空的,整条生产鉴权流程没有入口可用。</para>
/// <para>照抄官方示例(learn.microsoft.com 的 customize-openapi,aspnetcore-10.0 版本)。</para>
/// </summary>
internal sealed class ScalarBearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            In = ParameterLocation.Header,
            BearerFormat = "JWT",
        };

        // 逐个 operation 挂安全要求:文档级 security 只是默认值,逐条声明才是 Scalar/生成器都认的形状。
        // 匿名端点(登录、验证码)也照挂——OpenAPI 的 security 是"可以带",不是"必须带",
        // 带上一个用不到的令牌不会被拒;反过来漏掉一条,调用方在 UI 里就点不通那一条。
        foreach (var pathItem in document.Paths.Values)
        {
            if (pathItem.Operations is null) continue;
            foreach (var operation in pathItem.Operations.Values)
            {
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
                });
            }
        }
        return Task.CompletedTask;
    }
}
