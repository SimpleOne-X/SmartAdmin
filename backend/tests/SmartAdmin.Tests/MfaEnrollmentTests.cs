using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// TOTP 自助绑定 / 恢复 / 管理员清除 / 高敏默认(ADR 0006)。
/// 使用 <c>Totp:Enabled=true</c>。
/// </summary>
public class MfaEnrollmentTests
{
    private static AdminAppFactory Factory() =>
        new()
        {
            Settings = new Dictionary<string, string?>
            {
                ["SmartAdmin:Security:Totp:Enabled"] = "true",
                ["SmartAdmin:Security:DataProtection:Key"] =
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["SmartAdmin:Security:DataProtection:KeyVersion"] = "1",
            },
            Overrides = s =>
            {
                foreach (var d in s.ToList())
                {
                    if (d.ServiceType != typeof(IHostedService)) continue;
                    var name = d.ImplementationType?.Name
                               ?? d.ImplementationInstance?.GetType().Name
                               ?? "";
                    if (name.Contains("SecurityStartupDiagnostic", StringComparison.Ordinal))
                        s.Remove(d);
                }
            },
        };

    /// <summary>
    /// <paramref name="tenantId"/> 默认 null:后台 DI 作用域没有租户上下文,插入 AOP 填不了 TenantId,
    /// 落成 null 与其余同样在后台作用域直调服务的自助绑定用例一致(ClearFilter&lt;ITenantScoped&gt;() 已让
    /// StartBindAsync/CompleteBindAsync/UseRecoveryCodeAsync 不再关心这一列的值)。仅当用例要让这个用户
    /// 被一个真实已认证 HTTP 调用方(如登录态 superAdmin,租户固定为 1)按租户过滤看到时,才显式传入。
    /// </summary>
    private static async Task<(AdminAppFactory f, SysUser user, string password)> SeedUserAsync(
        AdminAppFactory f, bool superAdmin = false, bool forceTotp = false, long? tenantId = null)
    {
        using var scope = f.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IRepository<SysUser>>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var password = "TestPass123!";
        var user = new SysUser
        {
            Account = "mfa_user_" + Guid.NewGuid().ToString("N")[..8],
            Password = hasher.Hash(password),
            Name = "MFA Test",
            Enabled = true,
            IsSuperAdmin = superAdmin,
            ForceTotp = forceTotp,
            MustChangePassword = false,
            LastPasswordChangeTime = DateTime.Now,
            TenantId = tenantId,
        };
        await users.InsertAsync(user);
        return (f, user, password);
    }

    /// <summary>
    /// 经 HTTP 以 <paramref name="c"/>(须已带已认证 Bearer)清除目标用户 MFA;处理 [RequireReauth] 的一次重试。
    /// </summary>
    private static async Task ClearMfaViaHttpAsync(HttpClient c, long targetUserId)
    {
        async Task<bool> TryClearAsync()
        {
            var resp = await c.PostJson("/api/v1/sys/mfa/clear", new { userId = targetUserId });
            var raw = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(raw))
                return resp.IsSuccessStatusCode;
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var code = doc.RootElement.GetProperty("code").GetInt32();
            if (code == (int)ErrorCode.ReauthRequired)
            {
                var reauthEnv = await (await c.PostJson("/api/v1/auth/reauth", new
                {
                    method = "password",
                    password = "Test@123456",
                })).ReadEnvelope();
                Assert.Equal(0, reauthEnv.GetProperty("code").GetInt32());
                return false; // 调用方再试一次 clear
            }
            Assert.Equal(0, code);
            return true;
        }

        if (!await TryClearAsync())
            Assert.True(await TryClearAsync());
    }

    private static async Task BindSelfAsync(
        IMfaEnrollmentService enroll, ITotpService totp, string account, string password)
    {
        var start = await enroll.StartBindAsync(new TotpBindStartInput
        {
            Account = account,
            CurrentPassword = password,
        });
        await enroll.CompleteBindAsync(new TotpBindCompleteInput
        {
            BindChallengeId = start.BindChallengeId,
            TotpCode = totp.ComputeCode(start.Seed),
        });
    }

    [Fact]
    public async Task Self_bind_roundtrip_encrypts_seed_and_issues_recovery_codes()
    {
        await using var f = Factory();
        var (_, user, password) = await SeedUserAsync(f);
        using var scope = f.Services.CreateScope();
        var enroll = scope.ServiceProvider.GetRequiredService<IMfaEnrollmentService>();
        var totp = scope.ServiceProvider.GetRequiredService<ITotpService>();
        var users = scope.ServiceProvider.GetRequiredService<IRepository<SysUser>>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();

        var start = await enroll.StartBindAsync(new TotpBindStartInput
        {
            Account = user.Account,
            CurrentPassword = password,
        });
        Assert.False(string.IsNullOrEmpty(start.BindChallengeId));
        Assert.StartsWith("otpauth://", start.OtpauthUri);

        var complete = await enroll.CompleteBindAsync(new TotpBindCompleteInput
        {
            BindChallengeId = start.BindChallengeId,
            TotpCode = totp.ComputeCode(start.Seed),
        });
        Assert.Equal(10, complete.RecoveryCodes.Count);

        // user 是后台作用域直建的夹具(TenantId 默认 null),这里同样是没有租户上下文的后台作用域读回,须跨租户查找。
        var reloaded = await users.AsQueryable().ClearFilter<ITenantScoped>().Where(u => u.Id == user.Id).FirstAsync();
        Assert.NotNull(reloaded);
        Assert.True(reloaded!.TotpEnabled);
        Assert.False(string.IsNullOrEmpty(reloaded.TotpSeedProtected));
        var roundtrip = protector.Unprotect(reloaded.TotpSeedProtected!);
        Assert.Equal(start.Seed, roundtrip);
    }

    [Fact]
    public async Task Self_bind_rejects_wrong_password()
    {
        await using var f = Factory();
        var (_, user, _) = await SeedUserAsync(f);
        using var scope = f.Services.CreateScope();
        var enroll = scope.ServiceProvider.GetRequiredService<IMfaEnrollmentService>();

        var ex = await Assert.ThrowsAsync<AdminException>(() =>
            enroll.StartBindAsync(new TotpBindStartInput
            {
                Account = user.Account,
                CurrentPassword = "wrong-password",
            }));
        Assert.Equal(ErrorCode.PasswordWrong, ex.Code);
    }

    [Fact]
    public async Task Self_bind_rejects_when_already_bound()
    {
        await using var f = Factory();
        var (_, user, password) = await SeedUserAsync(f);
        using var scope = f.Services.CreateScope();
        var enroll = scope.ServiceProvider.GetRequiredService<IMfaEnrollmentService>();
        var totp = scope.ServiceProvider.GetRequiredService<ITotpService>();

        await BindSelfAsync(enroll, totp, user.Account, password);

        var ex = await Assert.ThrowsAsync<AdminException>(() =>
            enroll.StartBindAsync(new TotpBindStartInput
            {
                Account = user.Account,
                CurrentPassword = password,
            }));
        Assert.Equal(ErrorCode.MfaBindInvalid, ex.Code);
    }

    [Fact]
    public async Task Recovery_code_clears_mfa_and_revokes_sessions()
    {
        await using var f = Factory();
        var (_, user, password) = await SeedUserAsync(f);
        using var scope = f.Services.CreateScope();
        var enroll = scope.ServiceProvider.GetRequiredService<IMfaEnrollmentService>();
        var totp = scope.ServiceProvider.GetRequiredService<ITotpService>();
        var users = scope.ServiceProvider.GetRequiredService<IRepository<SysUser>>();

        var start = await enroll.StartBindAsync(new TotpBindStartInput
        {
            Account = user.Account,
            CurrentPassword = password,
        });
        var complete = await enroll.CompleteBindAsync(new TotpBindCompleteInput
        {
            BindChallengeId = start.BindChallengeId,
            TotpCode = totp.ComputeCode(start.Seed),
        });

        await enroll.UseRecoveryCodeAsync(new TotpRecoveryInput
        {
            Account = user.Account,
            CurrentPassword = password,
            RecoveryCode = complete.RecoveryCodes[0],
        });

        var reloaded = await users.AsQueryable().ClearFilter<ITenantScoped>().Where(u => u.Id == user.Id).FirstAsync();
        Assert.False(reloaded!.TotpEnabled);
        Assert.True(string.IsNullOrEmpty(reloaded.TotpSeedProtected));
    }

    [Fact]
    public async Task Admin_clear_mfa_allows_rebind()
    {
        await using var f = Factory();
        // ClearUserMfaAsync 的 op/target 查找故意保持按租户过滤(越权守卫,见该方法上的注释)——
        // target 须落在与下面发起 clear 的已登录 superAdmin 相同的租户(1),且必须真的经 HTTP 以
        // 已认证身份调用,才是这条保护路径的真实调用场景(而不是绕开 [RolePermission]/令牌直调服务)。
        var (_, target, password) = await SeedUserAsync(f, tenantId: DefaultTenantSeed.DEFAULT_TENANT_ID);
        using var scope = f.Services.CreateScope();
        var enroll = scope.ServiceProvider.GetRequiredService<IMfaEnrollmentService>();
        var totp = scope.ServiceProvider.GetRequiredService<ITotpService>();
        var users = scope.ServiceProvider.GetRequiredService<IRepository<SysUser>>();

        await BindSelfAsync(enroll, totp, target.Account, password);

        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await c.LoginToken("superAdmin", "Test@123456"));
        await ClearMfaViaHttpAsync(c, target.Id);

        // target.TenantId 显式落到租户 1(见上),但这里是没有租户上下文的后台作用域,须跨租户查找。
        var reloaded = await users.AsQueryable().ClearFilter<ITenantScoped>().Where(u => u.Id == target.Id).FirstAsync();
        Assert.False(reloaded!.TotpEnabled);

        // 可再次自助绑定
        await BindSelfAsync(enroll, totp, target.Account, password);
        reloaded = await users.AsQueryable().ClearFilter<ITenantScoped>().Where(u => u.Id == target.Id).FirstAsync();
        Assert.True(reloaded!.TotpEnabled);
    }

    [Fact]
    public async Task High_sensitivity_defaults_include_mfa_clear_not_invite()
    {
        await using var f = Factory();
        using var scope = f.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IHighSensitivityPermissionService>();
        var list = await svc.ListAsync();

        Assert.Contains(HighSensitivityPermissions.MfaClear, list.Defaults);
        Assert.DoesNotContain(list.Defaults, c => c.Contains("/mfa/invite", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(list.Defaults, c => c.Contains("/mfa/reset", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Policy_requires_force_totp_when_enabled()
    {
        await using var f = Factory();
        var (_, user, _) = await SeedUserAsync(f, forceTotp: true);
        using var scope = f.Services.CreateScope();
        var policy = scope.ServiceProvider.GetRequiredService<IMfaPolicyService>();
        Assert.True(await policy.IsMfaRequiredAsync(user));
    }

    [Fact]
    public async Task Policy_does_not_force_when_totp_feature_off()
    {
        await using var f = new AdminAppFactory
        {
            Settings = new Dictionary<string, string?>
            {
                ["SmartAdmin:Security:Totp:Enabled"] = "false",
                ["SmartAdmin:Security:DataProtection:Key"] =
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            },
        };
        var (_, user, _) = await SeedUserAsync(f, forceTotp: true);
        using var scope = f.Services.CreateScope();
        var policy = scope.ServiceProvider.GetRequiredService<IMfaPolicyService>();
        Assert.False(await policy.IsMfaRequiredAsync(user));
    }

    [Fact]
    public async Task Http_self_bind_endpoints()
    {
        await using var f = Factory();
        var (_, user, password) = await SeedUserAsync(f);
        using var scope = f.Services.CreateScope();
        var totp = scope.ServiceProvider.GetRequiredService<ITotpService>();

        var c = f.CreateClient();
        var startEnv = await (await c.PostJson("/api/v1/auth/mfa/bind/start", new
        {
            account = user.Account,
            currentPassword = password,
        })).ReadEnvelope();
        Assert.Equal(0, startEnv.GetProperty("code").GetInt32());
        var data = startEnv.GetProperty("data");
        var challengeId = data.GetProperty("bindChallengeId").GetString()!;
        var seed = data.GetProperty("seed").GetString()!;

        var completeEnv = await (await c.PostJson("/api/v1/auth/mfa/bind/complete", new
        {
            bindChallengeId = challengeId,
            totpCode = totp.ComputeCode(seed),
        })).ReadEnvelope();
        Assert.Equal(0, completeEnv.GetProperty("code").GetInt32());
        Assert.True(completeEnv.GetProperty("data").GetProperty("recoveryCodes").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Self_bind_rejects_when_totp_feature_off()
    {
        await using var f = new AdminAppFactory
        {
            Settings = new Dictionary<string, string?>
            {
                ["SmartAdmin:Security:Totp:Enabled"] = "false",
                ["SmartAdmin:Security:DataProtection:Key"] =
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            },
        };
        var (_, user, password) = await SeedUserAsync(f);
        using var scope = f.Services.CreateScope();
        var enroll = scope.ServiceProvider.GetRequiredService<IMfaEnrollmentService>();

        var ex = await Assert.ThrowsAsync<AdminException>(() =>
            enroll.StartBindAsync(new TotpBindStartInput
            {
                Account = user.Account,
                CurrentPassword = password,
            }));
        Assert.Equal(ErrorCode.NoPermission, ex.Code);
    }

    [Fact]
    public async Task Policy_require_for_super_admin_when_configured()
    {
        await using var f = new AdminAppFactory
        {
            Settings = new Dictionary<string, string?>
            {
                ["SmartAdmin:Security:Totp:Enabled"] = "true",
                ["SmartAdmin:Security:Totp:RequireForSuperAdmin"] = "true",
                ["SmartAdmin:Security:DataProtection:Key"] =
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            },
        };
        var (_, super, _) = await SeedUserAsync(f, superAdmin: true);
        var (_, normal, _) = await SeedUserAsync(f, superAdmin: false);
        using var scope = f.Services.CreateScope();
        var policy = scope.ServiceProvider.GetRequiredService<IMfaPolicyService>();

        Assert.True(await policy.IsMfaRequiredAsync(super));
        Assert.False(await policy.IsMfaRequiredAsync(normal));
    }

    [Fact]
    public async Task Clear_mfa_non_super_without_permission_denied()
    {
        await using var f = Factory();
        var (_, target, password) = await SeedUserAsync(f);
        var (_, operatorUser, _) = await SeedUserAsync(f);
        using var scope = f.Services.CreateScope();
        var enroll = scope.ServiceProvider.GetRequiredService<IMfaEnrollmentService>();
        var totp = scope.ServiceProvider.GetRequiredService<ITotpService>();

        await BindSelfAsync(enroll, totp, target.Account, password);

        var ex = await Assert.ThrowsAsync<AdminException>(() =>
            enroll.ClearUserMfaAsync(target.Id, operatorUser.Id));
        // 未绑 TOTP 的非超管:先撞 TotpNotBound;若操作人无 TOTP 且非超管
        Assert.True(ex.Code is ErrorCode.TotpNotBound or ErrorCode.NoPermission);
    }

    [Fact]
    public async Task Http_clear_mfa_as_super_admin()
    {
        await using var f = Factory();
        // target 须与下面登录的 superAdmin 同租户(1)——ClearUserMfaAsync 的 target 查找故意保持
        // 按租户过滤,见该方法上的注释。
        var (_, target, password) = await SeedUserAsync(f, tenantId: DefaultTenantSeed.DEFAULT_TENANT_ID);
        using var scope = f.Services.CreateScope();
        var enroll = scope.ServiceProvider.GetRequiredService<IMfaEnrollmentService>();
        var totp = scope.ServiceProvider.GetRequiredService<ITotpService>();
        var users = scope.ServiceProvider.GetRequiredService<IRepository<SysUser>>();

        await BindSelfAsync(enroll, totp, target.Account, password);

        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await c.LoginToken("superAdmin", "Test@123456"));

        // 超管默认无 TOTP:ClearUserMfa 允许未绑 TOTP 的超管清理他人(Totp:Enabled → RequireReauth 生效,
        // ClearMfaViaHttpAsync 内处理一次 reauth 重试)。
        await ClearMfaViaHttpAsync(c, target.Id);

        // target.TenantId 显式落到租户 1(见上),但这里是没有租户上下文的后台作用域,须跨租户查找。
        var reloaded = await users.AsQueryable().ClearFilter<ITenantScoped>().Where(u => u.Id == target.Id).FirstAsync();
        Assert.False(reloaded!.TotpEnabled);
    }
}
