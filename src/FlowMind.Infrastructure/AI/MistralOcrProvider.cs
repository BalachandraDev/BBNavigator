using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowMind.Application.Interfaces;
using Microsoft.Extensions.Options;

namespace FlowMind.Infrastructure.AI;

/// <summary>
/// Mistral OCR (POST {base}/v1/ocr). Sends only the requested pages via the `pages` parameter (0-based on the wire).
/// Quota-type failures (402/403/quota messages) raise <see cref="QuotaExceededException"/> and are never retried;
/// 429/5xx are retried with exponential backoff, then surface as <see cref="TransientProviderException"/>.
/// </summary>
public sealed class MistralOcrProvider : IOcrProvider
{
    public const string ProviderName = "mistral";
    private const int MaxHttpAttempts = 3;

    private readonly HttpClient _http;
    private readonly MistralOcrOptions _opt;
    private readonly TimeSpan _baseDelay;

    public MistralOcrProvider(HttpClient http, IOptions<AiProvidersOptions> options, TimeSpan? baseDelay = null)
    {
        _http = http;
        _opt = options.Value.MistralOcr;
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(2);
    }

    public string Name => ProviderName;

    public async Task<IReadOnlyList<OcrPageResult>> OcrAsync(Stream document, string contentType, IReadOnlyCollection<int>? pages, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
            throw new ProviderNotConfiguredException(Name, "Mistral OCR API key is not configured (AiProviders:MistralOcr:ApiKey).");

        using var ms = new MemoryStream();
        await document.CopyToAsync(ms, ct);
        var dataUrl = $"data:{contentType};base64,{Convert.ToBase64String(ms.ToArray())}";
        var isImage = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

        var body = new Dictionary<string, object?>
        {
            ["model"] = _opt.Model,
            ["document"] = isImage
                ? new { type = "image_url", image_url = dataUrl }
                : new { type = "document_url", document_url = dataUrl },
            ["include_image_base64"] = false,
            ["table_format"] = "markdown"
        };
        var requested = pages?.OrderBy(p => p).Distinct().ToList();
        if (requested is { Count: > 0 }) body["pages"] = BuildPagesParam(requested);

        var response = await SendWithRetryAsync(body, ct);
        var result = await response.Content.ReadFromJsonAsync<OcrResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty OCR response.");

        var returned = result.Pages ?? new();

        // Map by order rather than trusting the response's index base (docs are ambiguous: 0- vs 1-based).
        if (requested is { Count: > 0 })
        {
            if (returned.Count != requested.Count)
                throw new InvalidOperationException($"OCR returned {returned.Count} pages but {requested.Count} were requested.");
            return returned.Select((p, i) => new OcrPageResult(requested[i], p.Markdown ?? "")).ToList();
        }
        return returned.Select((p, i) => new OcrPageResult(i + 1, p.Markdown ?? "")).ToList();
    }

    /// <summary>1-based page numbers → Mistral's 0-based range string, e.g. [1,3,4,5] → "0,2-4".</summary>
    public static string BuildPagesParam(IReadOnlyList<int> sortedOneBased)
    {
        var parts = new List<string>();
        int i = 0;
        while (i < sortedOneBased.Count)
        {
            int start = sortedOneBased[i], end = start;
            while (i + 1 < sortedOneBased.Count && sortedOneBased[i + 1] == end + 1) { end = sortedOneBased[++i]; }
            parts.Add(start == end ? $"{start - 1}" : $"{start - 1}-{end - 1}");
            i++;
        }
        return string.Join(",", parts);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(object body, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_opt.BaseUrl), "v1/ocr"))
            {
                Content = JsonContent.Create(body)
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.ApiKey);

            HttpResponseMessage res;
            try { res = await _http.SendAsync(req, ct); }
            catch (HttpRequestException) when (attempt < MaxHttpAttempts)
            {
                await Task.Delay(Backoff(attempt, null), ct);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new TransientProviderException("Could not reach Mistral OCR.", ex);
            }

            if (res.IsSuccessStatusCode) return res;

            var text = await res.Content.ReadAsStringAsync(ct);
            var status = res.StatusCode;
            var retryAfter = res.Headers.RetryAfter?.Delta;
            res.Dispose();

            if (status is HttpStatusCode.PaymentRequired or HttpStatusCode.Forbidden
                || text.Contains("quota", StringComparison.OrdinalIgnoreCase))
                throw new QuotaExceededException(Name, "Mistral OCR quota/credits exhausted or access denied. Retry later; no paid fallback was used.");

            if (status == HttpStatusCode.Unauthorized)
                throw new ProviderNotConfiguredException(Name, "Mistral OCR rejected the API key.");

            if (status == HttpStatusCode.TooManyRequests || (int)status >= 500)
            {
                if (attempt >= MaxHttpAttempts)
                    throw new TransientProviderException($"Mistral OCR temporarily unavailable ({(int)status}).");
                await Task.Delay(Backoff(attempt, retryAfter), ct);
                continue;
            }

            throw new InvalidOperationException($"Mistral OCR request failed ({(int)status}).");
        }
    }

    private TimeSpan Backoff(int attempt, TimeSpan? retryAfter)
    {
        var exp = TimeSpan.FromTicks(_baseDelay.Ticks * (1L << (attempt - 1)));
        return retryAfter is { } ra && ra < TimeSpan.FromSeconds(30) ? ra : exp;
    }

    private sealed class OcrResponse
    {
        [JsonPropertyName("pages")] public List<OcrPage>? Pages { get; set; }
    }
    private sealed class OcrPage { [JsonPropertyName("markdown")] public string? Markdown { get; set; } }
}
