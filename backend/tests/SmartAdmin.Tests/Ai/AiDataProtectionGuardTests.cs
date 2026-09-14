using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>
/// 数据保护密钥为进程内临时密钥(<see cref="IDataProtectionKeyProvider.IsEphemeral"/> = true)时,
/// 保存 AI 厂商 Key 必须拒绝(49030)——否则信封重启后解不开,Key 静默失效却查不出原因。
/// </summary>
public class AiDataProtectionGuardTests
{
    private sealed class EphemeralKeyProvider : IDataProtectionKeyProvider
    {
        public bool IsEphemeral => true;
        public DataProtectionKeyMaterial GetCurrentKey() => throw new NotSupportedException("本测试不应真的走到加密这一步");
        public DataProtectionKeyMaterial GetKey(int version) => throw new NotSupportedException();
    }

    private static AdminAppFactory Factory() => new()
    {
        Overrides = s => s.Replace(ServiceDescriptor.Singleton<IDataProtectionKeyProvider>(new EphemeralKeyProvider())),
    };

    [Fact]
    public async Task Adding_provider_with_api_key_is_rejected_when_key_is_ephemeral()
    {
        using var f = Factory();
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAiProviderService>();

        var ex = await Assert.ThrowsAsync<AdminException>(() => service.AddAsync(new AiProviderAddInput(
            $"ephemeral-{Guid.NewGuid():N}", "Ephemeral", "deepseek", "https://api.deepseek.com/v1", "bearer",
            "sk-should-not-save", 1, null, [])));
        Assert.Equal(ErrorCode.AiDataProtectionKeyMissing, ex.Code);
    }

    [Fact]
    public async Task Adding_provider_without_api_key_still_succeeds_when_key_is_ephemeral()
    {
        // 不填 Key(ApplyApiKey 提前 return)不该受 IsEphemeral 影响——只有"真要落库一把新 Key"才需要拒绝。
        using var f = Factory();
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAiProviderService>();

        var id = await service.AddAsync(new AiProviderAddInput(
            $"ephemeral-nokey-{Guid.NewGuid():N}", "Ephemeral No Key", "ollama", "http://localhost:11434/v1", "none",
            null, 1, null, []));

        var view = await service.GetAsync(id);
        Assert.False(view.HasApiKey);
    }

    [Fact]
    public async Task Updating_provider_with_new_api_key_is_rejected_when_key_is_ephemeral()
    {
        using var f = Factory();
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAiProviderService>();

        // 新增时不带 Key(才能先落地这一行),再在更新时尝试补一把 Key——这条路径也要挡
        var code = $"ephemeral-update-{Guid.NewGuid():N}";
        var id = await service.AddAsync(new AiProviderAddInput(
            code, "Ephemeral Update", "deepseek", "https://api.deepseek.com/v1", "bearer", null, 1, null, []));

        var ex = await Assert.ThrowsAsync<AdminException>(() => service.UpdateAsync(id, new AiProviderUpdateInput(
            code, "Ephemeral Update", "https://api.deepseek.com/v1", "bearer", "sk-should-not-save", 1, null, [])));
        Assert.Equal(ErrorCode.AiDataProtectionKeyMissing, ex.Code);
    }
}
