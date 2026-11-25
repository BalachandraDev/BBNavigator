using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowMind.Application.Interfaces;
using Microsoft.Extensions.Options;

namespace FlowMind.Infrastructure.AI;

/// <summary>
/// Gemini as the chat model for Tamil (Google's models are the strongest free option for Indian languages). Same API key, same pacing
/// and the same FREE daily quota as document analysis: the usage guard counts both together, and no paid model is ever used.
/// A quota 429 is never retried; per-minute 429s and 5xx back off a few times.
/// </summary>
public sealed class GeminiChatProvider : IMultilingualChatProvider
{
    public const string ProviderName = GeminiAnalysisProvider.ProviderName;
    private const int MaxHttpAttempts = 3;

    private readonly HttpClient _http;
    private readonly GeminiOptions _opt;
    private readonly ProviderThrottle _throttle;
    private readonly TimeSpan _baseDelay;

    public GeminiChatProvider(HttpClient http, IOptions<AiProvidersOptions> options, ProviderThrottle throttle, TimeSpan? baseDelay = null)
    {
        _http = http; _opt = options.Value.Gemini; _throttle = throttle; _baseDelay = baseDelay ?? TimeSpan.FromSeconds(4);
    }

    public string Name => ProviderName;
    public string Model => string.IsNullOrWhiteSpace(_opt.ChatModel) ? _opt.Model : _opt.ChatModel;

    public async Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessageDto> messages, int maxOutputTokens, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
            throw new ProviderNotConfiguredException(Name, "Gemini API key is not configured (AiProviders:Gemini:ApiKey).");

        var body = BuildBody(messages, maxOutputTokens);
        var minInterval = TimeSpan.FromSeconds(60.0 / Math.Max(1, _opt.RequestsPerMinute));
        var url = new Uri(new Uri(_opt.BaseUrl), $"v1beta/models/{Model}:generateContent");

        for (int attempt = 1; ; attempt++)
        {
            await _throttle.WaitAsync(minInterval, ct);
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            req.Headers.Add("x-goog-api-key", _opt.ApiKey);

            HttpResponseMessage res;
            try { res = await _http.SendAsync(req, ct); }
            catch (HttpRequestException) when (attempt < MaxHttpAttempts) { await Task.Delay(Backoff(attempt, null), ct); continue; }
            catch (HttpRequestException ex) { throw new TransientProviderException("Could not reach Gemini.", ex); }

            using (res)
            {
                var text = await res.Content.ReadAsStringAsync(ct);
                if (res.IsSuccessStatusCode) return Parse(text);

                var status = res.StatusCode;
                var retryAfter = res.Headers.RetryAfter?.Delta ?? GeminiAnalysisProvider.ParseRetryDelay(text);
                if (status == HttpStatusCode.TooManyRequests)
                {
                    if (GeminiAnalysisProvider.LooksLikeDailyQuota(text))
                        throw new QuotaExceededException(Name, "Gemini daily free quota exhausted. Retry after the daily reset (midnight Pacific); no paid fallback was used.");
                    if (attempt >= MaxHttpAttempts)
                        throw new QuotaExceededException(Name, "Gemini rate limit still exceeded after retries. Retry later; no paid fallback was used.", DateTime.UtcNow.AddMinutes(2));
                    await Task.Delay(Backoff(attempt, retryAfter), ct);
                    continue;
                }
                if ((int)status >= 500)
                {
                    if (attempt >= MaxHttpAttempts) throw new TransientProviderException($"Gemini temporarily unavailable ({(int)status}).");
                    await Task.Delay(Backoff(attempt, retryAfter), ct);
                    continue;
                }
                if (status == HttpStatusCode.Forbidden)
                    throw new ProviderNotConfiguredException(Name, "Gemini denied access (403): check the API key, project and free-tier restrictions.");
                if (status == HttpStatusCode.BadRequest && text.Contains("API key", StringComparison.OrdinalIgnoreCase))
                    throw new ProviderNotConfiguredException(Name, "Gemini rejected the API key.");
                throw new InvalidOperationException($"Gemini chat request failed ({(int)status}).");
            }
        }
    }

    /// <summary>System messages become systemInstruction; assistant turns are "model" turns; neighbouring turns of one role are merged (Gemini wants alternation).</summary>
    internal static object BuildBody(IReadOnlyList<ChatMessageDto> messages, int maxOutputTokens)
    {
        var system = string.Join("\n\n", messages.Where(m => m.Role == "system").Select(m => m.Content));
        var turns = new List<(string Role, StringBuilder Text)>();
        foreach (var m in messages.Where(m => m.Role != "system"))
        {
            var role = m.Role == "assistant" ? "model" : "user";
            if (turns.Count > 0 && turns[^1].Role == role) turns[^1].Text.Append("\n\n").Append(m.Content);
            else turns.Add((role, new StringBuilder(m.Content)));
        }
        return new
        {
            systemInstruction = new { parts = new[] { new { text = system } } },
            contents = turns.Select(t => new { role = t.Role, parts = new[] { new { text = t.Text.ToString() } } }).ToArray(),
            generationConfig = new { temperature = 0, maxOutputTokens }
        };
    }

    internal static ChatCompletionResult Parse(string responseBody)
    {
        GeminiChatResponse? r;
        try { r = JsonSerializer.Deserialize<GeminiChatResponse>(responseBody); }
        catch (JsonException ex) { throw new InvalidAnalysisOutputException("Gemini returned an unreadable response.", ex); }

        var cand = r?.Candidates?.FirstOrDefault();
        if (cand == null)
            throw new InvalidAnalysisOutputException($"Gemini returned no answer{(r?.PromptFeedback?.BlockReason is { } b ? $" (blocked: {b})" : "")}.");
        var sb = new StringBuilder();
        foreach (var p in cand.Content?.Parts ?? new()) if (p.Thought != true && p.Text != null) sb.Append(p.Text);
        var answer = sb.ToString().Trim();
        if (answer.Length == 0)
            throw new InvalidAnalysisOutputException(string.Equals(cand.FinishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase)
                ? "Gemini used its whole output budget before answering. Raise Chat:TamilMaxOutputTokens."
                : "Gemini returned an empty answer.");
        return new ChatCompletionResult(answer, r!.UsageMetadata?.PromptTokenCount ?? 0, r.UsageMetadata?.CandidatesTokenCount ?? 0);
    }

    private TimeSpan Backoff(int attempt, TimeSpan? hint)
    {
        var exp = TimeSpan.FromTicks(_baseDelay.Ticks * (1L << (attempt - 1)));
        return hint is { } h && h < TimeSpan.FromSeconds(90) ? h : exp;
    }

    private sealed class GeminiChatResponse
    {
        [JsonPropertyName("candidates")] public List<Candidate>? Candidates { get; set; }
        [JsonPropertyName("usageMetadata")] public Usage? UsageMetadata { get; set; }
        [JsonPropertyName("promptFeedback")] public Feedback? PromptFeedback { get; set; }
    }
    private sealed class Candidate
    {
        [JsonPropertyName("content")] public ContentDto? Content { get; set; }
        [JsonPropertyName("finishReason")] public string? FinishReason { get; set; }
    }
    private sealed class ContentDto { [JsonPropertyName("parts")] public List<Part>? Parts { get; set; } }
    private sealed class Part
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("thought")] public bool? Thought { get; set; }
    }
    private sealed class Usage
    {
        [JsonPropertyName("promptTokenCount")] public int? PromptTokenCount { get; set; }
        [JsonPropertyName("candidatesTokenCount")] public int? CandidatesTokenCount { get; set; }
    }
    private sealed class Feedback { [JsonPropertyName("blockReason")] public string? BlockReason { get; set; } }
}
