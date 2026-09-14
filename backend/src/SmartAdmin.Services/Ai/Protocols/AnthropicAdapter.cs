using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// Anthropic 原生协议适配器,对应 <c>endpoint.Protocol == "anthropic"</c>。
/// system 消息不进 messages 数组,抽出后拼进请求体顶层 system 字段;max_tokens 是 Anthropic 的必填项,未指定时兜底 4096。
/// </summary>
public class AnthropicAdapter(IHttpClientFactory httpClientFactory) : IAiProtocolAdapter
{
    private const string AnthropicVersion = "2023-06-01";
    private const int DefaultMaxTokens = 4096;

    public string Protocol => "anthropic";

    public virtual async Task<AiChatResponse> ChatAsync(AiEndpoint endpoint, AiChatRequest request, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(AiHttpClient.Name);
        var body = BuildBody(endpoint, request, stream: false);

        using var httpRequest = BuildHttpRequest(endpoint, body);
        using var response = await client.SendAsync(httpRequest, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var root = JsonNode.Parse(payload)?.AsObject()
            ?? throw new JsonException("Anthropic 响应不是合法 JSON 对象");

        if (root["content"] is not JsonArray contentArray)
        {
            throw new JsonException("Anthropic 响应缺少 content 数组");
        }

        var content = ExtractText(contentArray);
        var requestId = root["id"]?.GetValue<string>();
        var finishReason = root["stop_reason"]?.GetValue<string>();
        var usage = TryParseUsage(root["usage"]) ?? new AiUsage(0, 0, AiUsageSource.Missing);

        return new AiChatResponse(content, endpoint.ProviderCode, endpoint.Model, usage, finishReason, 0, requestId);
    }

    public virtual async IAsyncEnumerable<AiChatChunk> StreamAsync(
        AiEndpoint endpoint,
        AiChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(AiHttpClient.Name);
        var body = BuildBody(endpoint, request, stream: true);

        using var httpRequest = BuildHttpRequest(endpoint, body);
        using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        // message_start 只报 input_tokens;output_tokens 要到 message_delta 才凑齐,先存局部变量等着
        var inputTokens = 0;

        // 不用 reader.EndOfStream(同步阻塞属性,CA2024 禁止在异步方法里用);靠 ReadLineAsync 返回 null 判断流结束
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var eventNode = JsonNode.Parse(line["data: ".Length..])?.AsObject()
                ?? throw new JsonException("Anthropic 流式响应不是合法 JSON 对象");

            var type = eventNode["type"]?.GetValue<string>()
                ?? throw new JsonException("Anthropic 流式响应缺少 type 字段");

            switch (type)
            {
                case "message_start":
                    inputTokens = eventNode["message"]?["usage"]?["input_tokens"]?.GetValue<int>()
                        ?? throw new JsonException("Anthropic message_start 缺少 message.usage.input_tokens");
                    break;

                case "content_block_delta":
                    if (eventNode["delta"]?["type"]?.GetValue<string>() == "text_delta")
                    {
                        var text = eventNode["delta"]?["text"]?.GetValue<string>() ?? string.Empty;
                        yield return new AiChatChunk(text, null, null);
                    }

                    break;

                case "message_delta":
                    var outputTokens = eventNode["usage"]?["output_tokens"]?.GetValue<int>()
                        ?? throw new JsonException("Anthropic message_delta 缺少 usage.output_tokens");
                    var stopReason = eventNode["delta"]?["stop_reason"]?.GetValue<string>();
                    yield return new AiChatChunk(string.Empty, new AiUsage(inputTokens, outputTokens, AiUsageSource.Reported), stopReason);
                    break;

                case "message_stop":
                    yield break;
            }
        }
    }

    private static JsonObject BuildBody(AiEndpoint endpoint, AiChatRequest request, bool stream)
    {
        var messages = new JsonArray();
        var systemText = new StringBuilder();

        foreach (var message in request.Messages)
        {
            if (message.Role == AiChatRole.System)
            {
                if (systemText.Length > 0)
                {
                    systemText.Append('\n');
                }

                systemText.Append(message.Content);
                continue;
            }

            messages.Add(new JsonObject
            {
                ["role"] = MapRole(message.Role),
                ["content"] = message.Content,
            });
        }

        var body = new JsonObject
        {
            ["model"] = endpoint.Model,
            ["max_tokens"] = request.MaxTokens ?? DefaultMaxTokens,
        };

        if (systemText.Length > 0)
        {
            body["system"] = systemText.ToString();
        }

        body["messages"] = messages;

        if (request.Temperature.HasValue)
        {
            body["temperature"] = request.Temperature.Value;
        }

        if (stream)
        {
            body["stream"] = true;
        }

        return body;
    }

    private static string MapRole(AiChatRole role) => role switch
    {
        AiChatRole.User => "user",
        AiChatRole.Assistant => "assistant",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    private static HttpRequestMessage BuildHttpRequest(AiEndpoint endpoint, JsonObject body)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.BaseUrl}/v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        httpRequest.Headers.TryAddWithoutValidation("x-api-key", endpoint.ApiKey);
        httpRequest.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);

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

    private static string ExtractText(JsonArray contentArray)
    {
        var builder = new StringBuilder();
        foreach (var node in contentArray)
        {
            if (node?["type"]?.GetValue<string>() != "text")
            {
                continue;
            }

            builder.Append(node["text"]?.GetValue<string>()
                ?? throw new JsonException("Anthropic 响应的 text 块缺少 text 字段"));
        }

        return builder.ToString();
    }

    private static AiUsage? TryParseUsage(JsonNode? usageNode)
    {
        if (usageNode is null)
        {
            return null;
        }

        var inputTokens = usageNode["input_tokens"]?.GetValue<int>() ?? 0;
        var outputTokens = usageNode["output_tokens"]?.GetValue<int>() ?? 0;
        return new AiUsage(inputTokens, outputTokens, AiUsageSource.Reported);
    }
}
