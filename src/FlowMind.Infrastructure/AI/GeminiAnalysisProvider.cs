using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowMind.Application.Interfaces;
using Microsoft.Extensions.Options;

namespace FlowMind.Infrastructure.AI;

/// <summary>Spaces out calls to a provider so free per-minute limits are respected. Singleton.</summary>
public sealed class ProviderThrottle
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _last = DateTime.MinValue;

    public async Task WaitAsync(TimeSpan minInterval, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _last + minInterval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _last = DateTime.UtcNow;
        }
        finally { _gate.Release(); }
    }
}

/// <summary>
/// Gemini Flash-Lite via generateContent. Requests JSON (responseMimeType) and carries the schema in the prompt;
/// the caller validates the result strictly. Daily-quota 429s are never retried; per-minute 429s and 5xx back off.
/// </summary>
public sealed class GeminiAnalysisProvider : IDocumentAnalysisProvider
{
    public const string ProviderName = "gemini";
    private const int MaxHttpAttempts = 3;

    private readonly HttpClient _http;
    private readonly GeminiOptions _opt;
    private readonly ProviderThrottle _throttle;
    private readonly TimeSpan _baseDelay;

    public GeminiAnalysisProvider(HttpClient http, IOptions<AiProvidersOptions> options, ProviderThrottle throttle, TimeSpan? baseDelay = null)
    {
        _http = http; _opt = options.Value.Gemini; _throttle = throttle;
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(4);
    }

    public string Name => ProviderName;

    public async Task<DocumentAnalysisResult> AnalyzeAsync(string systemPrompt, string content, string jsonSchema, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
            throw new ProviderNotConfiguredException(Name, "Gemini API key is not configured (AiProviders:Gemini:ApiKey).");

        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = systemPrompt + "\n\nRespond with JSON matching this schema exactly:\n" + jsonSchema } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = content } } } },
            generationConfig = new { temperature = 0, maxOutputTokens = _opt.MaxOutputTokens, responseMimeType = "application/json" }
        };

        var minInterval = TimeSpan.FromSeconds(60.0 / Math.Max(1, _opt.RequestsPerMinute));
        var url = new Uri(new Uri(_opt.BaseUrl), $"v1beta/models/{_opt.Model}:generateContent");

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
                var retryAfter = res.Headers.RetryAfter?.Delta ?? ParseRetryDelay(text);

                if (status == HttpStatusCode.TooManyRequests)
                {
                    // Daily quota exhausted: retrying cannot help. Per-minute limits: back off a few times, then stop.
                    if (LooksLikeDailyQuota(text))
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

                throw new InvalidOperationException($"Gemini request failed ({(int)status}).");
            }
        }
    }

    internal static DocumentAnalysisResult Parse(string responseBody)
    {
        GeminiResponse? r;
        try { r = JsonSerializer.Deserialize<GeminiResponse>(responseBody); }
        catch (JsonException ex) { throw new InvalidAnalysisOutputException("Gemini returned an unreadable response.", ex); }

        var cand = r?.Candidates?.FirstOrDefault();
        if (cand == null)
            throw new InvalidAnalysisOutputException($"Gemini returned no result{(r?.PromptFeedback?.BlockReason is { } b ? $" (blocked: {b})" : "")}.");
        if (string.Equals(cand.FinishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidAnalysisOutputException("Gemini output was truncated (MAX_TOKENS). Reduce Analysis:WindowChars or raise Gemini:MaxOutputTokens.");

        var sb = new StringBuilder();
        foreach (var p in cand.Content?.Parts ?? new()) if (p.Thought != true && p.Text != null) sb.Append(p.Text);
        var json = sb.ToString().Trim();
        if (json.Length == 0) throw new InvalidAnalysisOutputException("Gemini returned an empty result.");

        return new DocumentAnalysisResult(json, r!.UsageMetadata?.PromptTokenCount ?? 0, r.UsageMetadata?.CandidatesTokenCount ?? 0);
    }

    internal static bool LooksLikeDailyQuota(string body) =>
        body.Contains("PerDay", StringComparison.OrdinalIgnoreCase)
        || body.Contains("per day", StringComparison.OrdinalIgnoreCase)
        || body.Contains("daily", StringComparison.OrdinalIgnoreCase);

    internal static TimeSpan? ParseRetryDelay(string body)
    {
        // Google error details carry e.g. "retryDelay": "37s".
        var i = body.IndexOf("\"retryDelay\"", StringComparison.Ordinal);
        if (i < 0) return null;
        var m = System.Text.RegularExpressions.Regex.Match(body[i..], "\"retryDelay\"\\s*:\\s*\"(\\d+(?:\\.\\d+)?)s\"");
        return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var s)
            ? TimeSpan.FromSeconds(s) : null;
    }

    private TimeSpan Backoff(int attempt, TimeSpan? hint)
    {
        var exp = TimeSpan.FromTicks(_baseDelay.Ticks * (1L << (attempt - 1)));
        return hint is { } h && h < TimeSpan.FromSeconds(90) ? h : exp;
    }

    private sealed class GeminiResponse
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
