using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FlowMind.Application.Interfaces;
using Microsoft.Extensions.Options;

namespace FlowMind.Infrastructure.AI;

/// <summary>
/// Cloudflare Workers AI embeddings (@cf/baai/bge-m3). Request: {"text":[...]}. The response is read tolerantly
/// (result.data or result.response) and every vector is checked against the configured dimension.
/// Daily-allocation 429s are never retried; other 429s/5xx back off a few times.
/// </summary>
public sealed class CloudflareEmbeddingProvider : IEmbeddingProvider
{
    public const string ProviderName = "cloudflare";
    private const int MaxHttpAttempts = 3;

    private readonly HttpClient _http;
    private readonly CloudflareOptions _opt;
    private readonly TimeSpan _baseDelay;

    public CloudflareEmbeddingProvider(HttpClient http, IOptions<AiProvidersOptions> options, TimeSpan? baseDelay = null)
    {
        _http = http; _opt = options.Value.Cloudflare; _baseDelay = baseDelay ?? TimeSpan.FromSeconds(2);
    }

    public string Name => ProviderName;
    public int Dimensions => _opt.Dimensions;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();
        if (string.IsNullOrWhiteSpace(_opt.ApiToken) || string.IsNullOrWhiteSpace(_opt.AccountId))
            throw new ProviderNotConfiguredException(Name, "Cloudflare is not configured (AiProviders:Cloudflare:AccountId and ApiToken).");

        var url = new Uri(new Uri(_opt.BaseUrl), $"client/v4/accounts/{Uri.EscapeDataString(_opt.AccountId)}/ai/run/{_opt.Model}");

        for (int attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new { text = texts }) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.ApiToken);

            HttpResponseMessage res;
            try { res = await _http.SendAsync(req, ct); }
            catch (HttpRequestException) when (attempt < MaxHttpAttempts) { await Task.Delay(Backoff(attempt), ct); continue; }
            catch (HttpRequestException ex) { throw new TransientProviderException("Could not reach Cloudflare.", ex); }

            using (res)
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                if (res.IsSuccessStatusCode) return Parse(body, texts.Count, _opt.Dimensions);

                if (res.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (LooksLikeDailyQuota(body))
                        throw new QuotaExceededException(Name, "Cloudflare daily free allocation (neurons) exhausted. It resets at 00:00 UTC; no paid fallback was used.",
                            DateTime.UtcNow.Date.AddDays(1));
                    if (attempt >= MaxHttpAttempts)
                        throw new QuotaExceededException(Name, "Cloudflare rate limit still exceeded after retries. Retry later.", DateTime.UtcNow.AddMinutes(2));
                    await Task.Delay(Backoff(attempt), ct);
                    continue;
                }
                if ((int)res.StatusCode >= 500)
                {
                    if (attempt >= MaxHttpAttempts) throw new TransientProviderException($"Cloudflare temporarily unavailable ({(int)res.StatusCode}).");
                    await Task.Delay(Backoff(attempt), ct);
                    continue;
                }
                if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new ProviderNotConfiguredException(Name, $"Cloudflare denied access ({(int)res.StatusCode}): check the account id and API token permissions (Workers AI).");

                throw new InvalidOperationException($"Cloudflare embedding request failed ({(int)res.StatusCode}).");
            }
        }
    }

    internal static IReadOnlyList<float[]> Parse(string body, int expectedCount, int dimensions)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
                throw new InvalidAnalysisOutputException("Cloudflare reported an unsuccessful embedding call.");

            var result = root.TryGetProperty("result", out var r) ? r : root;
            JsonElement data;
            if (!(result.TryGetProperty("data", out data) || result.TryGetProperty("response", out data)) || data.ValueKind != JsonValueKind.Array)
                throw new InvalidAnalysisOutputException("Cloudflare embedding response had no vectors.");

            var list = new List<float[]>();
            foreach (var row in data.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array) throw new InvalidAnalysisOutputException("Cloudflare embedding response was malformed.");
                var v = new float[row.GetArrayLength()];
                int i = 0;
                foreach (var n in row.EnumerateArray()) v[i++] = n.GetSingle();
                if (v.Length != dimensions)
                    throw new InvalidAnalysisOutputException($"Embedding has {v.Length} dimensions but {dimensions} are configured (AiProviders:Cloudflare:Dimensions and the vector table must match).");
                list.Add(v);
            }
            if (list.Count != expectedCount)
                throw new InvalidAnalysisOutputException($"Expected {expectedCount} embeddings, received {list.Count}.");
            return list;
        }
        catch (JsonException ex) { throw new InvalidAnalysisOutputException("Cloudflare returned an unreadable response.", ex); }
        catch (InvalidOperationException ex) { throw new InvalidAnalysisOutputException("Cloudflare returned an unexpected response shape.", ex); }
        catch (FormatException ex) { throw new InvalidAnalysisOutputException("Cloudflare returned non-numeric vector values.", ex); }
    }

    private static bool LooksLikeDailyQuota(string body) =>
        body.Contains("neurons", StringComparison.OrdinalIgnoreCase)
        || body.Contains("daily", StringComparison.OrdinalIgnoreCase)
        || body.Contains("4006", StringComparison.Ordinal);

    private TimeSpan Backoff(int attempt) => TimeSpan.FromTicks(_baseDelay.Ticks * (1L << (attempt - 1)));
}
