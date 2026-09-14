using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// AI 网关(<see cref="AiChatClient"/>)+ 两个协议适配器的桩 HttpMessageHandler 测试。
/// 不走 HTTP 端点(批次 3 才有控制器),直接从 DI 解析 <see cref="IAiChatClient"/> 调用。
/// </summary>
public class AiChatClientTests
{
    private static AdminAppFactory Factory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        IReadOnlyDictionary<string, string?>? settings = null) =>
        new()
        {
            Settings = settings,
            Overrides = services => services.AddHttpClient(AiHttpClient.Name)
                .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(respond)),
        };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private static async Task<(SysAiProvider Provider, SysAiModel Model)> SeedProviderAsync(
        IServiceProvider sp, string protocol,
        string modelName = "test-model", bool providerEnabled = true, bool modelEnabled = true, bool isDefault = true,
        string authScheme = "bearer", string? apiKey = "sk-test")
    {
        var providers = sp.GetRequiredService<IRepository<SysAiProvider>>();
        var models = sp.GetRequiredService<IRepository<SysAiModel>>();
        var protector = sp.GetRequiredService<ISecretProtector>();

        var provider = new SysAiProvider
        {
            Code = $"test-{protocol}-{Guid.NewGuid():N}",
            Name = "Test Provider",
            Preset = protocol,
            Protocol = protocol,
            BaseUrl = "https://fake-ai.test",
            AuthScheme = authScheme,
            ApiKeyProtected = apiKey is null ? null : protector.Protect(apiKey),
            ApiKeyHint = apiKey is null ? null : apiKey[^Math.Min(4, apiKey.Length)..],
            Enabled = providerEnabled,
        };
        await providers.InsertAsync(provider);

        var model = new SysAiModel
        {
            ProviderId = provider.Id,
            Name = modelName,
            DisplayName = modelName,
            Enabled = modelEnabled,
            IsDefault = isDefault,
        };
        await models.InsertAsync(model);

        return (provider, model);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    };

    // ── OpenAI 兼容协议 ──────────────────────────────────────────────

    [Fact]
    public async Task OpenAi_non_streaming_success_records_reported_usage()
    {
        const string responseBody = """
            {"id":"chatcmpl-1","choices":[{"message":{"role":"assistant","content":"你好"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":2,"total_tokens":7}}
            """;
        HttpRequestMessage? captured = null;
        using var f = Factory(async (req, ct) =>
        {
            captured = req;
            return Json(HttpStatusCode.OK, responseBody);
        });
        using var scope = f.Services.CreateScope();
        var (provider, model) = await SeedProviderAsync(scope.ServiceProvider, "openai");

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();
        var response = await client.ChatAsync(new AiChatRequest
        {
            Scene = "unit.test",
            Messages = [AiChatMessage.User("你好")],
            ProviderCode = provider.Code,
            Model = model.Name,
        });

        Assert.Equal("你好", response.Content);
        Assert.Equal("stop", response.FinishReason);
        Assert.Equal(5, response.Usage.InputTokens);
        Assert.Equal(2, response.Usage.OutputTokens);
        Assert.Equal(AiUsageSource.Reported, response.Usage.Source);
        Assert.Equal($"https://fake-ai.test/chat/completions", captured!.RequestUri!.ToString());
        Assert.Equal("Bearer sk-test", captured.Headers.Authorization?.ToString());

        var logs = await scope.ServiceProvider.GetRequiredService<IRepository<SysAiUsageLog>>()
            .AsQueryable().Where(l => l.ProviderId == provider.Id).ToListAsync();
        var log = Assert.Single(logs);
        Assert.True(log.Success);
        Assert.Equal(7, log.TotalTokens);
        Assert.Equal(1, log.UsageSource);
        Assert.False(log.Streamed);
    }

    [Fact]
    public async Task OpenAi_streaming_yields_deltas_and_final_usage_chunk()
    {
        const string sse = """
            data: {"id":"c1","choices":[{"delta":{"content":"Hello"},"finish_reason":null}]}

            data: {"id":"c1","choices":[{"delta":{"content":" world"},"finish_reason":"stop"}]}

            data: {"id":"c1","choices":[],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}

            data: [DONE]

            """;
        using var f = Factory((req, ct) => Task.FromResult(Sse(sse)));
        using var scope = f.Services.CreateScope();
        var (provider, model) = await SeedProviderAsync(scope.ServiceProvider, "openai");

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();
        var chunks = new List<AiChatChunk>();
        await foreach (var chunk in client.StreamAsync(new AiChatRequest
        {
            Scene = "unit.test",
            Messages = [AiChatMessage.User("hi")],
            ProviderCode = provider.Code,
            Model = model.Name,
        }))
        {
            chunks.Add(chunk);
        }

        Assert.Equal("Hello world", string.Concat(chunks.Select(c => c.Delta)));
        var usageChunk = Assert.Single(chunks, c => c.Usage is not null);
        Assert.Equal(3, usageChunk.Usage!.InputTokens);
        Assert.Equal(2, usageChunk.Usage.OutputTokens);

        var log = Assert.Single(await scope.ServiceProvider.GetRequiredService<IRepository<SysAiUsageLog>>()
            .AsQueryable().Where(l => l.ProviderId == provider.Id).ToListAsync());
        Assert.True(log.Streamed);
        Assert.Equal(1, log.UsageSource);   // Reported
        Assert.Equal(5, log.TotalTokens);
    }

    [Fact]
    public async Task OpenAi_streaming_without_usage_records_missing()
    {
        const string sse = """
            data: {"id":"c1","choices":[{"delta":{"content":"hi"},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        using var f = Factory((req, ct) => Task.FromResult(Sse(sse)));
        using var scope = f.Services.CreateScope();
        var (provider, model) = await SeedProviderAsync(scope.ServiceProvider, "openai");

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();
        await foreach (var _ in client.StreamAsync(new AiChatRequest
        {
            Scene = "unit.test",
            Messages = [AiChatMessage.User("hi")],
            ProviderCode = provider.Code,
            Model = model.Name,
        }))
        {
        }

        var log = Assert.Single(await scope.ServiceProvider.GetRequiredService<IRepository<SysAiUsageLog>>()
            .AsQueryable().Where(l => l.ProviderId == provider.Id).ToListAsync());
        Assert.Equal(2, log.UsageSource);   // Missing
        Assert.Equal(0, log.TotalTokens);
    }

    [Fact]
    public async Task OpenAi_stream_options_retries_once_without_it_after_400()
    {
        const string sse = """
            data: {"id":"c1","choices":[{"delta":{"content":"ok"},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        var attempts = new List<string>();
        using var f = Factory(async (req, ct) =>
        {
            var body = await req.Content!.ReadAsStringAsync(ct);
            attempts.Add(body);
            if (body.Contains("stream_options", StringComparison.Ordinal))
                return Json(HttpStatusCode.BadRequest, """{"error":"stream_options not supported"}""");
            return Sse(sse);
        });
        using var scope = f.Services.CreateScope();
        var (provider, model) = await SeedProviderAsync(scope.ServiceProvider, "openai");

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();
        var chunks = new List<AiChatChunk>();
        await foreach (var chunk in client.StreamAsync(new AiChatRequest
        {
            Scene = "unit.test",
            Messages = [AiChatMessage.User("hi")],
            ProviderCode = provider.Code,
            Model = model.Name,
        }))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(2, attempts.Count);
        Assert.Contains("stream_options", attempts[0], StringComparison.Ordinal);
        Assert.DoesNotContain("stream_options", attempts[1], StringComparison.Ordinal);
        Assert.Equal("ok", string.Concat(chunks.Select(c => c.Delta)));
    }

    // ── Anthropic 协议 ───────────────────────────────────────────────

    [Fact]
    public async Task Anthropic_non_streaming_success()
    {
        const string responseBody = """
            {"id":"msg_1","content":[{"type":"text","text":"你好"}],"stop_reason":"end_turn","usage":{"input_tokens":4,"output_tokens":3}}
            """;
        HttpRequestMessage? captured = null;
        using var f = Factory(async (req, ct) =>
        {
            captured = req;
            return Json(HttpStatusCode.OK, responseBody);
        });
        using var scope = f.Services.CreateScope();
        var (provider, model) = await SeedProviderAsync(scope.ServiceProvider, "anthropic", authScheme: "x-api-key");

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();
        var response = await client.ChatAsync(new AiChatRequest
        {
            Scene = "unit.test",
            Messages = [AiChatMessage.System("system prompt"), AiChatMessage.User("你好")],
            ProviderCode = provider.Code,
            Model = model.Name,
        });

        Assert.Equal("你好", response.Content);
        Assert.Equal("end_turn", response.FinishReason);
        Assert.Equal(4, response.Usage.InputTokens);
        Assert.Equal(3, response.Usage.OutputTokens);
        Assert.Equal($"https://fake-ai.test/v1/messages", captured!.RequestUri!.ToString());
        Assert.Equal("sk-test", captured.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", captured.Headers.GetValues("anthropic-version").Single());
    }

    [Fact]
    public async Task Anthropic_streaming_yields_deltas_and_final_usage_chunk()
    {
        const string sse = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":6,"output_tokens":0}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hi"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" there"}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":4}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        using var f = Factory((req, ct) => Task.FromResult(Sse(sse)));
        using var scope = f.Services.CreateScope();
        var (provider, model) = await SeedProviderAsync(scope.ServiceProvider, "anthropic", authScheme: "x-api-key");

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();
        var chunks = new List<AiChatChunk>();
        await foreach (var chunk in client.StreamAsync(new AiChatRequest
        {
            Scene = "unit.test",
            Messages = [AiChatMessage.User("hi")],
            ProviderCode = provider.Code,
            Model = model.Name,
        }))
        {
            chunks.Add(chunk);
        }

        Assert.Equal("Hi there", string.Concat(chunks.Select(c => c.Delta)));
        var usageChunk = Assert.Single(chunks, c => c.Usage is not null);
        Assert.Equal(6, usageChunk.Usage!.InputTokens);
        Assert.Equal(4, usageChunk.Usage.OutputTokens);
        Assert.Equal("end_turn", usageChunk.FinishReason);
    }

    // ── 上游错误映射 ─────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ErrorCode.AiUpstreamAuthFailed)]
    [InlineData(HttpStatusCode.Forbidden, ErrorCode.AiUpstreamAuthFailed)]
    [InlineData(HttpStatusCode.TooManyRequests, ErrorCode.AiUpstreamRateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, ErrorCode.AiUpstreamError)]
    public async Task Upstream_http_errors_map_to_expected_code_and_are_logged(HttpStatusCode status, ErrorCode expected)
    {
        using var f = Factory((req, ct) => Task.FromResult(Json(status, """{"error":"boom"}""")));
        using var scope = f.Services.CreateScope();
        var (provider, model) = await SeedProviderAsync(scope.ServiceProvider, "openai");

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();
        var ex = await Assert.ThrowsAsync<AdminException>(() => client.ChatAsync(new AiChatRequest
        {
            Scene = "unit.test",
            Messages = [AiChatMessage.User("hi")],
            ProviderCode = provider.Code,
            Model = model.Name,
        }));
        Assert.Equal(expected, ex.Code);

        var log = Assert.Single(await scope.ServiceProvider.GetRequiredService<IRepository<SysAiUsageLog>>()
            .AsQueryable().Where(l => l.ProviderId == provider.Id).ToListAsync());
        Assert.False(log.Success);
        Assert.Equal((int)expected, log.ErrorCode);
    }

    [Fact]
    public async Task Malformed_json_response_maps_to_bad_response_code()
    {
        using var f = Factory((req, ct) => Task.FromResult(Json(HttpStatusCode.OK, "{not-json")));
        using var scope = f.Services.CreateScope();
        var (provider, model) = await SeedProviderAsync(scope.ServiceProvider, "openai");

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();
        var ex = await Assert.ThrowsAsync<AdminException>(() => client.ChatAsync(new AiChatRequest
        {
            Scene = "unit.test",
            Messages = [AiChatMessage.User("hi")],
            ProviderCode = provider.Code,
            Model = model.Name,
        }));
        Assert.Equal(ErrorCode.AiUpstreamBadResponse, ex.Code);
    }

    [Fact]
    public async Task Timeout_maps_to_upstream_timeout_and_caller_cancellation_does_not()
    {
        using var f = Factory(
            async (req, ct) => { await Task.Delay(TimeSpan.FromSeconds(10), ct); return Json(HttpStatusCode.OK, "{}"); },
            settings: new Dictionary<string, string?> { ["SmartAdmin:Ai:TimeoutSeconds"] = "1" });
        using var scope = f.Services.CreateScope();
        var (provider, model) = await SeedProviderAsync(scope.ServiceProvider, "openai");

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();
        var ex = await Assert.ThrowsAsync<AdminException>(() => client.ChatAsync(new AiChatRequest
        {
            Scene = "unit.test",
            Messages = [AiChatMessage.User("hi")],
            ProviderCode = provider.Code,
            Model = model.Name,
        }));
        Assert.Equal(ErrorCode.AiUpstreamTimeout, ex.Code);
    }

    // ── 解析与校验规则 ───────────────────────────────────────────────

    [Fact]
    public async Task Empty_scene_throws_scene_required()
    {
        using var f = Factory((req, ct) => Task.FromResult(Json(HttpStatusCode.OK, "{}")));
        using var scope = f.Services.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();

        var ex = await Assert.ThrowsAsync<AdminException>(() => client.ChatAsync(new AiChatRequest
        {
            Scene = "  ",
            Messages = [AiChatMessage.User("hi")],
        }));
        Assert.Equal(ErrorCode.AiSceneRequired, ex.Code);
    }

    [Fact]
    public async Task No_default_model_throws_when_nothing_specified()
    {
        using var f = Factory((req, ct) => Task.FromResult(Json(HttpStatusCode.OK, "{}")));
        using var scope = f.Services.CreateScope();
        await SeedProviderAsync(scope.ServiceProvider, "openai", isDefault: false);   // 有厂商有模型,但没有全局默认模型
        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();

        var ex = await Assert.ThrowsAsync<AdminException>(() => client.ChatAsync(new AiChatRequest
        {
            Scene = "unit.test",
            Messages = [AiChatMessage.User("hi")],
        }));
        Assert.Equal(ErrorCode.AiNoDefaultModel, ex.Code);
    }

    [Fact]
    public async Task Only_model_given_finds_first_enabled_provider_carrying_it()
    {
        const string responseBody = """
            {"id":"c1","choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
            """;
        using var f = Factory((req, ct) => Task.FromResult(Json(HttpStatusCode.OK, responseBody)));
        using var scope = f.Services.CreateScope();
        var (provider, model) = await SeedProviderAsync(scope.ServiceProvider, "openai", modelName: "shared-name", isDefault: false);

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();
        var response = await client.ChatAsync(new AiChatRequest
        {
            Scene = "unit.test",
            Messages = [AiChatMessage.User("hi")],
            Model = "shared-name",
        });
        Assert.Equal("ok", response.Content);
        Assert.Equal(provider.Code, response.ProviderCode);
    }

    [Fact]
    public async Task Disabled_provider_throws_provider_disabled_even_if_code_matches()
    {
        using var f = Factory((req, ct) => Task.FromResult(Json(HttpStatusCode.OK, "{}")));
        using var scope = f.Services.CreateScope();
        var (provider, model) = await SeedProviderAsync(scope.ServiceProvider, "openai", providerEnabled: false);

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();
        var ex = await Assert.ThrowsAsync<AdminException>(() => client.ChatAsync(new AiChatRequest
        {
            Scene = "unit.test",
            Messages = [AiChatMessage.User("hi")],
            ProviderCode = provider.Code,
            Model = model.Name,
        }));
        Assert.Equal(ErrorCode.AiProviderDisabled, ex.Code);
    }

    [Fact]
    public async Task Unknown_provider_code_throws_provider_not_found()
    {
        using var f = Factory((req, ct) => Task.FromResult(Json(HttpStatusCode.OK, "{}")));
        using var scope = f.Services.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();

        var ex = await Assert.ThrowsAsync<AdminException>(() => client.ChatAsync(new AiChatRequest
        {
            Scene = "unit.test",
            Messages = [AiChatMessage.User("hi")],
            ProviderCode = "does-not-exist",
            Model = "whatever",
        }));
        Assert.Equal(ErrorCode.AiProviderNotFound, ex.Code);
    }

    // ── 多模态内容(issue #7) ────────────────────────────────────────

    private static readonly IReadOnlyList<AiChatContentPart> MultimodalParts =
    [
        new AiChatContentPart.Text("描述这张图"),
        new AiChatContentPart.Image(new AiImageSource.Base64("image/png", "QQ==")),
    ];

    [Fact]
    public async Task OpenAi_multimodal_message_serializes_text_and_base64_image_parts()
    {
        const string responseBody = """
            {"id":"c1","choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
            """;
        string? capturedBody = null;
        using var f = Factory(async (req, ct) =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync(ct);
            return Json(HttpStatusCode.OK, responseBody);
        });
        using var scope = f.Services.CreateScope();
        var (provider, model) = await SeedProviderAsync(scope.ServiceProvider, "openai");

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();
        await client.ChatAsync(new AiChatRequest
        {
            Scene = "unit.test",
            Messages = [AiChatMessage.User(MultimodalParts)],
            ProviderCode = provider.Code,
            Model = model.Name,
        });

        using var body = JsonDocument.Parse(capturedBody!);
        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("描述这张图", content[0].GetProperty("text").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,QQ==", content[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task OpenAi_multimodal_message_serializes_url_image_source()
    {
        const string responseBody = """
            {"id":"c1","choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
            """;
        string? capturedBody = null;
        using var f = Factory(async (req, ct) =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync(ct);
            return Json(HttpStatusCode.OK, responseBody);
        });
        using var scope = f.Services.CreateScope();
        var (provider, model) = await SeedProviderAsync(scope.ServiceProvider, "openai");

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();
        await client.ChatAsync(new AiChatRequest
        {
            Scene = "unit.test",
            Messages = [AiChatMessage.User([new AiChatContentPart.Image(new AiImageSource.Url("https://example.com/a.png"))])],
            ProviderCode = provider.Code,
            Model = model.Name,
        });

        using var body = JsonDocument.Parse(capturedBody!);
        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("https://example.com/a.png", content[0].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task OpenAi_response_schema_serializes_response_format_json_schema()
    {
        const string responseBody = """
            {"id":"c1","choices":[{"message":{"role":"assistant","content":"{}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
            """;
        string? capturedBody = null;
        using var f = Factory(async (req, ct) =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync(ct);
            return Json(HttpStatusCode.OK, responseBody);
        });
        using var scope = f.Services.CreateScope();
        var (provider, model) = await SeedProviderAsync(scope.ServiceProvider, "openai");

        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { ok = new { type = "boolean" } },
            required = new[] { "ok" },
            additionalProperties = false,
        });

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();
        await client.ChatAsync(new AiChatRequest
        {
            Scene = "approval.summary",
            Messages = [AiChatMessage.User("hi")],
            ProviderCode = provider.Code,
            Model = model.Name,
            ResponseSchema = schema,
        });

        using var body = JsonDocument.Parse(capturedBody!);
        var jsonSchema = body.RootElement.GetProperty("response_format").GetProperty("json_schema");
        Assert.Equal("json_schema", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal("approval_summary", jsonSchema.GetProperty("name").GetString());
        Assert.True(jsonSchema.GetProperty("strict").GetBoolean());
        Assert.Equal("object", jsonSchema.GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Anthropic_multimodal_message_serializes_text_and_base64_image_parts()
    {
        const string responseBody = """
            {"id":"msg_1","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}
            """;
        string? capturedBody = null;
        using var f = Factory(async (req, ct) =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync(ct);
            return Json(HttpStatusCode.OK, responseBody);
        });
        using var scope = f.Services.CreateScope();
        var (provider, model) = await SeedProviderAsync(scope.ServiceProvider, "anthropic", authScheme: "x-api-key");

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();
        await client.ChatAsync(new AiChatRequest
        {
            Scene = "unit.test",
            Messages = [AiChatMessage.User(MultimodalParts)],
            ProviderCode = provider.Code,
            Model = model.Name,
        });

        using var body = JsonDocument.Parse(capturedBody!);
        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        var source = content[1].GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("image/png", source.GetProperty("media_type").GetString());
        Assert.Equal("QQ==", source.GetProperty("data").GetString());
    }

    [Fact]
    public async Task Anthropic_response_schema_serializes_output_config_format()
    {
        const string responseBody = """
            {"id":"msg_1","content":[{"type":"text","text":"{}"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}
            """;
        string? capturedBody = null;
        using var f = Factory(async (req, ct) =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync(ct);
            return Json(HttpStatusCode.OK, responseBody);
        });
        using var scope = f.Services.CreateScope();
        var (provider, model) = await SeedProviderAsync(scope.ServiceProvider, "anthropic", authScheme: "x-api-key");

        var schema = JsonSerializer.SerializeToElement(new { type = "object" });

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();
        await client.ChatAsync(new AiChatRequest
        {
            Scene = "unit.test",
            Messages = [AiChatMessage.User("hi")],
            ProviderCode = provider.Code,
            Model = model.Name,
            ResponseSchema = schema,
        });

        using var body = JsonDocument.Parse(capturedBody!);
        var format = body.RootElement.GetProperty("output_config").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("object", format.GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task System_message_with_parts_throws_multimodal_system_unsupported_without_calling_upstream()
    {
        var called = false;
        using var f = Factory((req, ct) =>
        {
            called = true;
            return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
        });
        using var scope = f.Services.CreateScope();
        var (provider, model) = await SeedProviderAsync(scope.ServiceProvider, "openai");

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();
        var ex = await Assert.ThrowsAsync<AdminException>(() => client.ChatAsync(new AiChatRequest
        {
            Scene = "unit.test",
            Messages = [AiChatMessage.System("sys") with { Parts = MultimodalParts }, AiChatMessage.User("hi")],
            ProviderCode = provider.Code,
            Model = model.Name,
        }));

        Assert.Equal(ErrorCode.AiMultimodalSystemUnsupported, ex.Code);
        Assert.False(called);
    }
}
