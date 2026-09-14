using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// OpenAI 兼容协议适配器。DeepSeek / 通义千问 / 智谱 / Kimi / 豆包 / Gemini / Azure OpenAI / Ollama /
/// 自定义厂商都走同一套 <c>/chat/completions</c> 请求响应结构,按 <c>endpoint.Protocol == "openai"</c> 匹配到这里。
/// </summary>
public class OpenAiCompatibleAdapter(IHttpClientFactory httpClientFactory) : IAiProtocolAdapter
{
    public string Protocol => "openai";

    public virtual async Task<AiChatResponse> ChatAsync(AiEndpoint endpoint, AiChatRequest request, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(AiHttpClient.Name);
        var body = BuildBody(endpoint, request, stream: false, includeStreamOptions: false);

        using var httpRequest = BuildHttpRequest(endpoint, body);
        using var response = await client.SendAsync(httpRequest, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var root = JsonNode.Parse(payload)?.AsObject()
            ?? throw new JsonException("OpenAI 兼容响应不是合法 JSON 对象");

        if (root["choices"] is not JsonArray { Count: > 0 } choices)
        {
            throw new JsonException("OpenAI 兼容响应缺少 choices 数组");
        }

        var content = choices[0]?["message"]?["content"]?.GetValue<string>()
            ?? throw new JsonException("OpenAI 兼容响应缺少 choices[0].message.content");

        var finishReason = choices[0]?["finish_reason"]?.GetValue<string>();
        var requestId = root["id"]?.GetValue<string>();
        var usage = TryParseUsage(root["usage"]) ?? new AiUsage(0, 0, AiUsageSource.Missing);

        return new AiChatResponse(content, endpoint.ProviderCode, endpoint.Model, usage, finishReason, 0, requestId);
    }

    public virtual async IAsyncEnumerable<AiChatChunk> StreamAsync(
        AiEndpoint endpoint,
        AiChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(AiHttpClient.Name);

        var body = BuildBody(endpoint, request, stream: true, includeStreamOptions: true);
        using var httpRequest = BuildHttpRequest(endpoint, body);
        var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode && response.StatusCode == HttpStatusCode.BadRequest)
        {
            // 豆包 / Kimi 等厂商的 /chat/completions 不认 stream_options 字段,首次请求 400 时去掉该字段重试一次
            response.Dispose();
            var retryBody = BuildBody(endpoint, request, stream: true, includeStreamOptions: false);
            using var retryRequest = BuildHttpRequest(endpoint, retryBody);
            response = await client.SendAsync(retryRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        using (response)
        {
            await EnsureSuccessAsync(response, cancellationToken);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            // 不用 reader.EndOfStream(同步阻塞属性,CA2024 禁止在异步方法里用);靠 ReadLineAsync 返回 null 判断流结束
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    continue;
                }

                var payload = line["data: ".Length..];
                if (payload == "[DONE]")
                {
                    yield break;
                }

                var chunkNode = JsonNode.Parse(payload)?.AsObject()
                    ?? throw new JsonException("OpenAI 兼容流式响应不是合法 JSON 对象");

                var delta = string.Empty;
                string? finishReason = null;
                if (chunkNode["choices"] is JsonArray { Count: > 0 } choices)
                {
                    delta = choices[0]?["delta"]?["content"]?.GetValue<string>() ?? string.Empty;
                    finishReason = choices[0]?["finish_reason"]?.GetValue<string>();
                }

                var usage = TryParseUsage(chunkNode["usage"]);
                yield return new AiChatChunk(delta, usage, finishReason);
            }
        }
    }

    private static JsonObject BuildBody(AiEndpoint endpoint, AiChatRequest request, bool stream, bool includeStreamOptions)
    {
        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            messages.Add(new JsonObject
            {
                ["role"] = MapRole(message.Role),
                ["content"] = message.Content,
            });
        }

        var body = new JsonObject
        {
            ["model"] = endpoint.Model,
            ["messages"] = messages,
            ["stream"] = stream,
        };

        if (request.Temperature.HasValue)
        {
            body["temperature"] = request.Temperature.Value;
        }

        if (request.MaxTokens.HasValue)
        {
            body["max_tokens"] = request.MaxTokens.Value;
        }

        if (stream && includeStreamOptions)
        {
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        return body;
    }

    private static string MapRole(AiChatRole role) => role switch
    {
        AiChatRole.System => "system",
        AiChatRole.User => "user",
        AiChatRole.Assistant => "assistant",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    private static HttpRequestMessage BuildHttpRequest(AiEndpoint endpoint, JsonObject body)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.BaseUrl}/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        switch (endpoint.AuthScheme)
        {
            case "bearer":
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
                break;
            case "api-key":
                httpRequest.Headers.TryAddWithoutValidation("api-key", endpoint.ApiKey);
                break;
        }

        return httpRequest;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var snippet = text.Length > 512 ? text[..512] : text;
        throw new AiUpstreamHttpException((int)response.StatusCode, snippet);
    }

    private static AiUsage? TryParseUsage(JsonNode? usageNode)
    {
        if (usageNode is null)
        {
            return null;
        }

        var inputTokens = usageNode["prompt_tokens"]?.GetValue<int>() ?? 0;
        var outputTokens = usageNode["completion_tokens"]?.GetValue<int>() ?? 0;
        return new AiUsage(inputTokens, outputTokens, AiUsageSource.Reported);
    }
}
