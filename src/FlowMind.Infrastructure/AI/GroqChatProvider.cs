using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowMind.Application.Interfaces;
using Microsoft.Extensions.Options;

namespace FlowMind.Infrastructure.AI;

/// <summary>
/// Groq chat (OpenAI-compatible /openai/v1/chat/completions, default model openai/gpt-oss-20b).
/// Daily-limit 429s (RPD/TPD) are never retried; per-minute 429s and 5xx back off a few times. No fallback model.
/// </summary>
public sealed class GroqChatProvider : IChatProvider
{
    public const string ProviderName = "groq";
    private const int MaxHttpAttempts = 3;

    private readonly HttpClient _http;
    private readonly GroqOptions _opt;
    private readonly ProviderThrottle _throttle = new(); // own pacing: independent of Gemini's
    private readonly TimeSpan _baseDelay;

    public GroqChatProvider(HttpClient http, IOptions<AiProvidersOptions> options, TimeSpan? baseDelay = null)
    {
        _http = http; _opt = options.Value.Groq; _baseDelay = baseDelay ?? TimeSpan.FromSeconds(3);
    }

    public string Name => ProviderName;

    public async Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessageDto> messages, int maxOutputTokens, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
            throw new ProviderNotConfiguredException(Name, "Groq API key is not configured (AiProviders:Groq:ApiKey).");

        var body = new Dictionary<string, object?>
        {
            ["model"] = _opt.Model,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            ["temperature"] = 0,
            ["max_completion_tokens"] = maxOutputTokens
        };
        if (!string.IsNullOrWhiteSpace(_opt.ReasoningEffort)) body["reasoning_effort"] = _opt.ReasoningEffort;

        var minInterval = TimeSpan.FromSeconds(60.0 / Math.Max(1, _opt.RequestsPerMinute));
        var url = new Uri(new Uri(_opt.BaseUrl), "openai/v1/chat/completions");

        for (int attempt = 1; ; attempt++)
        {
            await _throttle.WaitAsync(minInterval, ct);
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.ApiKey);

            HttpResponseMessage res;
            try { res = await _http.SendAsync(req, ct); }
            catch (HttpRequestException) when (attempt < MaxHttpAttempts) { await Task.Delay(Backoff(attempt, null), ct); continue; }
            catch (HttpRequestException ex) { throw new TransientProviderException("Could not reach Groq.", ex); }

            using (res)
            {
                var text = await res.Content.ReadAsStringAsync(ct);
                if (res.IsSuccessStatusCode) return Parse(text);

                var retryAfter = res.Headers.RetryAfter?.Delta;
                if (res.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (LooksLikeDailyLimit(text))
                        throw new QuotaExceededException(Name, "Groq daily free limit reached. Try again after it resets; no paid fallback was used.", DateTime.UtcNow.Date.AddDays(1));
                    // A long Retry-After means a window we should not sit through inside a user request.
                    if (attempt >= MaxHttpAttempts || retryAfter is { } ra && ra > TimeSpan.FromSeconds(30))
                        throw new QuotaExceededException(Name, "Groq rate limit reached. Try again in a minute; no paid fallback was used.", DateTime.UtcNow.AddMinutes(1));
                    await Task.Delay(Backoff(attempt, retryAfter), ct);
                    continue;
                }
                if ((int)res.StatusCode >= 500)
                {
                    if (attempt >= MaxHttpAttempts) throw new TransientProviderException($"Groq temporarily unavailable ({(int)res.StatusCode}).");
                    await Task.Delay(Backoff(attempt, retryAfter), ct);
                    continue;
                }
                if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new ProviderNotConfiguredException(Name, $"Groq denied access ({(int)res.StatusCode}): check the API key.");

                throw new InvalidOperationException($"Groq request failed ({(int)res.StatusCode}).");
            }
        }
    }

    internal static ChatCompletionResult Parse(string body)
    {
        GroqResponse? r;
        try { r = JsonSerializer.Deserialize<GroqResponse>(body); }
        catch (JsonException ex) { throw new InvalidAnalysisOutputException("Groq returned an unreadable response.", ex); }

        var choice = r?.Choices?.FirstOrDefault();
        var content = choice?.Message?.Content?.Trim();
        if (string.IsNullOrEmpty(content))
            throw new InvalidAnalysisOutputException(string.Equals(choice?.FinishReason, "length", StringComparison.OrdinalIgnoreCase)
                ? "Groq used its whole output budget before answering. Raise AiProviders:Groq:MaxOutputTokens."
                : "Groq returned an empty answer.");
        return new ChatCompletionResult(content, r!.Usage?.PromptTokens ?? 0, r.Usage?.CompletionTokens ?? 0);
    }

    private static bool LooksLikeDailyLimit(string body) =>
        body.Contains("per day", StringComparison.OrdinalIgnoreCase)
        || body.Contains("(TPD)", StringComparison.Ordinal) || body.Contains("(RPD)", StringComparison.Ordinal);

    private TimeSpan Backoff(int attempt, TimeSpan? hint) =>
        hint is { } h && h < TimeSpan.FromSeconds(30) ? h : TimeSpan.FromTicks(_baseDelay.Ticks * (1L << (attempt - 1)));

    private sealed class GroqResponse
    {
        [JsonPropertyName("choices")] public List<Choice>? Choices { get; set; }
        [JsonPropertyName("usage")] public UsageDto? Usage { get; set; }
    }
    private sealed class Choice
    {
        [JsonPropertyName("message")] public Msg? Message { get; set; }
        [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
    }
    private sealed class Msg { [JsonPropertyName("content")] public string? Content { get; set; } }
    private sealed class UsageDto
    {
        [JsonPropertyName("prompt_tokens")] public int? PromptTokens { get; set; }
        [JsonPropertyName("completion_tokens")] public int? CompletionTokens { get; set; }
    }
}
