using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FlowMind.API.Authentication;
using FlowMind.Application.Interfaces;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowMind.Tests;

public class ApiSecurityAndHealthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public ApiSecurityAndHealthTests(WebApplicationFactory<Program> factory) => _factory = factory.Configure();

    private HttpClient ClientWith(string role, Guid? tenant)
    {
        if (tenant is { } t) SeedTenant(_factory, t);
        var token = _factory.Services.GetRequiredService<TokenService>().Issue("test", role, tenant).Token;
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    /// <summary>JWT validation checks the company still exists and is active, so tests must create it first.</summary>
    internal static void SeedTenant(WebApplicationFactory<Program> f, Guid id, bool active = true)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowMind.Infrastructure.Database.FlowMindDbContext>();
        if (db.Tenants.Any(t => t.Id == id)) return;
        db.Tenants.Add(new FlowMind.Domain.Entities.Tenant { Id = id, Name = "T", Slug = "t-" + id.ToString("N")[..12], IsActive = active });
        db.SaveChanges();
    }

    [Fact]
    public async Task Live_is_open_and_returns_200() =>
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().GetAsync("/health/live")).StatusCode);

    [Fact]
    public async Task Ready_returns_503_when_dependencies_are_down() =>
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _factory.CreateClient().GetAsync("/health/ready")).StatusCode);

    [Fact]
    public async Task SystemInfo_is_anonymous() =>
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().GetAsync("/api/v1/system/info")).StatusCode);

    [Theory]
    [InlineData("/api/v1/documents")]
    [InlineData("/api/v1/applications")]
    [InlineData("/api/v1/usage")]
    public async Task Tenant_endpoints_require_authentication(string url) =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(url)).StatusCode);

    [Fact]
    public async Task Forged_token_signed_with_another_key_is_rejected()
    {
        var other = new TokenService(Microsoft.Extensions.Options.Options.Create(new AuthOptions { SigningKey = "ANOTHER-key-0123456789-abcdefghijklmnop" }));
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.Issue("x", Roles.TenantAdmin, Guid.NewGuid()).Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/documents")).StatusCode);
    }

    [Fact]
    public async Task Tenant_token_cannot_create_tenants() =>
        Assert.Equal(HttpStatusCode.Forbidden,
            (await ClientWith(Roles.TenantAdmin, Guid.NewGuid()).PostAsJsonAsync("/api/v1/tenants", new { name = "X Corp", slug = "x-corp" })).StatusCode);

    [Fact]
    public async Task Tenant_role_without_tenant_claim_is_forbidden_on_tenant_endpoints() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientWith(Roles.TenantAdmin, null).GetAsync("/api/v1/documents")).StatusCode);

    [Fact]
    public async Task Platform_admin_cannot_use_tenant_endpoints_without_a_tenant() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientWith(Roles.PlatformAdmin, null).GetAsync("/api/v1/documents")).StatusCode);

    [Fact]
    public async Task Wrong_credentials_return_generic_401_without_leaking_which_part_was_wrong()
    {
        var c = _factory.CreateClient();
        var wrongPlatform = await c.PostAsJsonAsync("/api/v1/auth/token", new { apiKey = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPlatform.StatusCode);
    }

    [Fact]
    public void Tenant_context_is_resolved_from_the_http_jwt_not_the_unscoped_default()
    {
        using var scope = _factory.Services.CreateScope();
        Assert.IsType<HttpTenantContext>(scope.ServiceProvider.GetRequiredService<ITenantContext>());
    }
}
