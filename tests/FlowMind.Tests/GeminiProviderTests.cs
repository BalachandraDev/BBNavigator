using System.Net;
using System.Text;
using System.Text.Json;
using FlowMind.Application.Interfaces;
using FlowMind.Infrastructure;
using FlowMind.Infrastructure.AI;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowMind.Tests;

public class GeminiProviderTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Code, string Body)> _responses = new();
        public List<(string Url, string Key, string Body)> Requests { get; } = new();
        public int Calls => Requests.Count;
        public FakeHandler Enqueue(HttpStatusCode code, string body = "{}") { _responses.Enqueue((code, body)); return this; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.GetValues("x-goog-api-key").First(), await request.Content!.ReadAsStringAsync(ct)));
            var (code, body) = _responses.Dequeue();
            return new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static GeminiAnalysisProvider Make(FakeHandler h, string key = "k") => new(
        new HttpClient(h),
        Options.Create(new AiProvidersOptions { Gemini = new GeminiOptions { ApiKey = key, Model = "gemini-3.1-flash-lite", RequestsPerMinute = 100000 } }),
        new ProviderThrottle(), TimeSpan.Zero);

    private const string Ok = """
        {"candidates":[{"content":{"parts":[{"text":"{\"modules\":[]}"}]},"finishReason":"STOP"}],
         "usageMetadata":{"promptTokenCount":120,"candidatesTokenCount":15,"totalTokenCount":135}}
        """;

    [Fact]
    public async Task Success_returns_json_and_token_counts_and_uses_header_auth_and_json_mode()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, Ok);
        var r = await Make(h).AnalyzeAsync("sys", "content", "{schema}", default);

        Assert.Equal("{\"modules\":[]}", r.Json);
        Assert.Equal((120, 15), (r.InputTokens, r.OutputTokens));
        var req = h.Requests.Single();
        Assert.Contains("models/gemini-3.1-flash-lite:generateContent", req.Url);
        Assert.DoesNotContain("key=", req.Url);           // key goes in a header, never in the URL
        Assert.Equal("k", req.Key);
        using var json = JsonDocument.Parse(req.Body);
        Assert.Equal("application/json", json.RootElement.GetProperty("generationConfig").GetProperty("responseMimeType").GetString());
        Assert.Contains("{schema}", json.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Thought_parts_are_ignored()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK,
            """{"candidates":[{"content":{"parts":[{"text":"thinking...","thought":true},{"text":"{}"}]},"finishReason":"STOP"}]}""");
        Assert.Equal("{}", (await Make(h).AnalyzeAsync("s", "c", "x", default)).Json);
    }

    [Fact]
    public async Task Truncated_output_is_rejected()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, """{"candidates":[{"content":{"parts":[{"text":"{\"mod"}]},"finishReason":"MAX_TOKENS"}]}""");
        await Assert.ThrowsAsync<InvalidAnalysisOutputException>(() => Make(h).AnalyzeAsync("s", "c", "x", default));
    }

    [Fact]
    public async Task Blocked_prompt_is_reported_as_invalid_output()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, """{"promptFeedback":{"blockReason":"SAFETY"}}""");
        await Assert.ThrowsAsync<InvalidAnalysisOutputException>(() => Make(h).AnalyzeAsync("s", "c", "x", default));
    }

    [Fact]
    public async Task Missing_key_makes_no_http_call()
    {
        var h = new FakeHandler();
        await Assert.ThrowsAsync<ProviderNotConfiguredException>(() => Make(h, "").AnalyzeAsync("s", "c", "x", default));
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task Daily_quota_429_is_not_retried()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.TooManyRequests,
            """{"error":{"code":429,"status":"RESOURCE_EXHAUSTED","message":"Quota exceeded for metric ... GenerateRequestsPerDayPerProjectPerModel-FreeTier"}}""");
        await Assert.ThrowsAsync<QuotaExceededException>(() => Make(h).AnalyzeAsync("s", "c", "x", default));
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Per_minute_429_backs_off_and_then_succeeds()
    {
        var h = new FakeHandler()
            .Enqueue(HttpStatusCode.TooManyRequests, """{"error":{"status":"RESOURCE_EXHAUSTED","message":"rate limit per minute"}}""")
            .Enqueue(HttpStatusCode.OK, Ok);
        await Make(h).AnalyzeAsync("s", "c", "x", default);
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task Persistent_429_stops_with_quota_exceeded_after_three_attempts()
    {
        var body = """{"error":{"status":"RESOURCE_EXHAUSTED","message":"slow down"}}""";
        var h = new FakeHandler().Enqueue(HttpStatusCode.TooManyRequests, body).Enqueue(HttpStatusCode.TooManyRequests, body).Enqueue(HttpStatusCode.TooManyRequests, body);
        await Assert.ThrowsAsync<QuotaExceededException>(() => Make(h).AnalyzeAsync("s", "c", "x", default));
        Assert.Equal(3, h.Calls);
    }

    [Fact]
    public async Task Server_errors_are_retried_then_surface_as_transient()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.ServiceUnavailable).Enqueue(HttpStatusCode.ServiceUnavailable).Enqueue(HttpStatusCode.ServiceUnavailable);
        await Assert.ThrowsAsync<TransientProviderException>(() => Make(h).AnalyzeAsync("s", "c", "x", default));
        Assert.Equal(3, h.Calls);
    }
}
