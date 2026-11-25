using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FlowMind.Tests;

public class FrontendTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public FrontendTests(WebApplicationFactory<Program> factory) => _factory = factory.Configure();

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Fact]
    public async Task Home_page_is_public_and_served_with_a_strict_content_security_policy()
    {
        var res = await _factory.CreateClient().GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("id=\"root\"", await res.Content.ReadAsStringAsync());

        var csp = string.Join(" ", res.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Theory]
    [InlineData("/js/main.js")]
    [InlineData("/js/api.js")]
    [InlineData("/js/dom.js")]
    [InlineData("/js/views/chat.js")]
    [InlineData("/css/tokens.css")]
    [InlineData("/css/base.css")]
    [InlineData("/css/components.css")]
    [InlineData("/css/chat.css")]
    [InlineData("/css/shell.css")]
    [InlineData("/css/views.css")]
    [InlineData("/css/bot.css")]
    public async Task Static_assets_are_served_without_signing_in(string path)
    {
        var res = await _factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/bot.html")]
    public async Task Every_stylesheet_a_page_links_exists_and_inline_styles_stay_out_of_the_markup(string page)
    {
        var client = _factory.CreateClient();
        var html = await (await client.GetAsync(page)).Content.ReadAsStringAsync();
        var links = System.Text.RegularExpressions.Regex.Matches(html, "<link rel=\"stylesheet\" href=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Contains("/css/tokens.css", links);                                      // every page uses the shared design tokens
        foreach (var l in links) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(l)).StatusCode);
        Assert.DoesNotContain("<style", html);                                           // the strict CSP allows no inline style
        Assert.DoesNotContain("style=\"", html.Replace("style=\"padding:16px\"", ""));  // (the noscript notice is the one allowed exception)
    }

    [Fact]
    public async Task Api_data_still_requires_sign_in()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v1/chat", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public void Frontend_source_never_inserts_untrusted_text_as_html_and_holds_no_provider_keys()
    {
        var dir = Path.GetDirectoryName(ThisFile());                 // tests/FlowMind.Tests
        while (dir != null && !Directory.Exists(Path.Combine(dir, "src", "FlowMind.API", "wwwroot"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var root = Path.Combine(dir!, "src", "FlowMind.API", "wwwroot");

        var banned = new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval(", "new Function(", "AiProviders", "apiKey:" };
        var files = Directory.GetFiles(root, "*.js", SearchOption.AllDirectories).Concat(Directory.GetFiles(root, "*.html", SearchOption.AllDirectories)).ToList();
        Assert.NotEmpty(files);
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            foreach (var b in banned.Where(b => !(b == "apiKey:" && f.EndsWith("login.js")))) // login sends the user's own key to /auth/token
                Assert.False(text.Contains(b), $"{Path.GetFileName(f)} contains '{b}'");
        }

        var html = File.ReadAllText(Path.Combine(root, "index.html"));
        Assert.DoesNotContain("<script>", html);                    // module script only, loaded from our own origin
        Assert.DoesNotContain("onclick=", html);
    }
}
