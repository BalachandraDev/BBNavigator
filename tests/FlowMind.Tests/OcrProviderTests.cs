using System.Net;
using System.Text;
using System.Text.Json;
using FlowMind.Application.Interfaces;
using FlowMind.Infrastructure;
using FlowMind.Infrastructure.AI;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowMind.Tests;

public class OcrProviderTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
        public List<string> Bodies { get; } = new();
        public int Calls { get; private set; }
        public FakeHandler Enqueue(HttpStatusCode code, string body = "{}") { _responses.Enqueue(_ => new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") }); return this; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return _responses.Dequeue()(request);
        }
    }

    private static MistralOcrProvider Make(FakeHandler h, string key = "k") => new(
        new HttpClient(h),
        Options.Create(new AiProvidersOptions { MistralOcr = new MistralOcrOptions { ApiKey = key } }),
        TimeSpan.Zero);

    private static Stream Doc() => new MemoryStream(new byte[] { 1, 2, 3 });

    [Theory]
    [InlineData(new[] { 1 }, "0")]
    [InlineData(new[] { 1, 3, 4, 5 }, "0,2-4")]
    [InlineData(new[] { 2, 3, 7 }, "1-2,6")]
    public void Pages_param_is_zero_based_ranges(int[] pages, string expected) =>
        Assert.Equal(expected, MistralOcrProvider.BuildPagesParam(pages));

    [Fact]
    public async Task Sends_only_requested_pages_and_maps_results_in_order()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, """{"pages":[{"index":0,"markdown":"A"},{"index":1,"markdown":"B"}]}""");
        var result = await Make(h).OcrAsync(Doc(), "application/pdf", new[] { 2, 5 }, default);

        Assert.Equal(new[] { (2, "A"), (5, "B") }, result.Select(r => (r.PageNumber, r.Markdown)));
        using var json = JsonDocument.Parse(h.Bodies[0]);
        Assert.Equal("1,4", json.RootElement.GetProperty("pages").GetString());
    }

    [Fact]
    public async Task Missing_api_key_throws_not_configured_without_any_http_call()
    {
        var h = new FakeHandler();
        await Assert.ThrowsAsync<ProviderNotConfiguredException>(() => Make(h, key: "").OcrAsync(Doc(), "application/pdf", new[] { 1 }, default));
        Assert.Equal(0, h.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Quota_style_errors_are_not_retried(HttpStatusCode code)
    {
        var h = new FakeHandler().Enqueue(code, "{}");
        await Assert.ThrowsAsync<QuotaExceededException>(() => Make(h).OcrAsync(Doc(), "application/pdf", new[] { 1 }, default));
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Temporary_errors_are_retried_with_backoff_then_succeed()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.TooManyRequests).Enqueue(HttpStatusCode.BadGateway)
            .Enqueue(HttpStatusCode.OK, """{"pages":[{"markdown":"ok"}]}""");
        var result = await Make(h).OcrAsync(Doc(), "application/pdf", new[] { 1 }, default);
        Assert.Single(result);
        Assert.Equal(3, h.Calls);
    }

    [Fact]
    public async Task Persistent_temporary_errors_surface_as_transient_after_max_attempts()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.BadGateway).Enqueue(HttpStatusCode.BadGateway).Enqueue(HttpStatusCode.BadGateway);
        await Assert.ThrowsAsync<TransientProviderException>(() => Make(h).OcrAsync(Doc(), "application/pdf", new[] { 1 }, default));
        Assert.Equal(3, h.Calls);
    }
}
