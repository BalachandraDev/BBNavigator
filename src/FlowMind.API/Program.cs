using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using FlowMind.API.Authentication;
using FlowMind.API.Controllers;
using FlowMind.Application.Interfaces;
using FlowMind.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// ---- Auth configuration (fail fast if the signing key is missing/weak) ----
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
builder.Services.Configure<UploadOptions>(builder.Configuration.GetSection(UploadOptions.Section));
var auth = builder.Configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
if (auth.SigningKey.Length < 32)
    throw new InvalidOperationException("Auth:SigningKey must be at least 32 characters (set it via environment variable Auth__SigningKey).");
ProductionGuards.Enforce(builder.Configuration, builder.Environment); // refuses dev secrets/databases outside Development
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<TokenStatusChecker>();
// Server-side secret used to keep each company key recoverable (encrypted) for the platform admin. Empty = nothing recoverable is stored.
// SecretBox (company-key and storage-credential encryption) is registered in AddInfrastructure so the background worker has it too.
builder.Services.AddScoped<FlowMind.API.Tenants.TenantAdminService>();

builder.Services.AddHttpContextAccessor();
// Registered BEFORE AddFlowMindInfrastructure so the tenant comes from the JWT, not the unscoped default.
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();
builder.Services.AddScoped<IAuditLogger, HttpAuditLogger>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddFlowMindInfrastructure(builder.Configuration);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false; // keep claim names exactly as issued ("tid", "role")
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = auth.Issuer,
            ValidateAudience = true, ValidAudience = auth.Audience,
            ValidateLifetime = true, ClockSkew = TimeSpan.FromMinutes(1),
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth.SigningKey)),
            RoleClaimType = ClaimNames.Role,
            NameClaimType = "sub"
        };
        o.Events = new JwtBearerEvents
        {
            // A revoked user key or a deactivated company stops working within seconds, not at token expiry.
            OnTokenValidated = async ctx =>
            {
                var db = ctx.HttpContext.RequestServices.GetRequiredService<FlowMind.Infrastructure.Database.FlowMindDbContext>();
                var checker = ctx.HttpContext.RequestServices.GetRequiredService<TokenStatusChecker>();
                if (ctx.Principal == null || !await checker.IsStillValidAsync(ctx.Principal, db, ctx.HttpContext.RequestAborted))
                    ctx.Fail("Access has been revoked.");
            }
        };
    })
    // Simple integrations: just send "X-Api-Key: <key>" (used by POST /api/ask). Revocation is checked on every request.
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthHandler>(ApiKeyAuthHandler.SchemeName, null);

builder.Services.AddAuthorization(o =>
{
    // Secure by default: every endpoint requires a signed-in caller unless marked [AllowAnonymous].
    o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    o.AddPolicy("PlatformAdmin", p => p.RequireRole(Roles.PlatformAdmin));
    o.AddPolicy("TenantAdmin", p => p.RequireRole(Roles.TenantAdmin).RequireClaim(ClaimNames.Tenant));
    // Admins and ordinary users: read approved knowledge and chat.
    o.AddPolicy("TenantMember", p => p.RequireRole(Roles.TenantAdmin, Roles.TenantUser).RequireClaim(ClaimNames.Tenant));
});

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "BBNavigator API", Version = "v1", Description = "AI-powered application knowledge assistant." });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
        Description = "Get a token from POST /api/v1/auth/token, then paste it here."
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = Array.Empty<string>()
    });
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
    // Brute-force protection for sign-in, forgot-password and reset. Per address, and a hospital often sits behind one address, so it is roomier than a single user needs; each account also locks itself after repeated wrong passwords.
    var authPerMinute = builder.Configuration.GetValue("Security:AuthRequestsPerMinute", 30);
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1) }));
    // Public share links: per link secret AND per caller address, so one visitor cannot burn a link's allowance.
    var botPerMinute = builder.Configuration.GetValue("Security:BotRequestsPerMinute", 8);
    o.AddPolicy("bot", ctx => RateLimitPartition.GetFixedWindowLimiter(
        "b:" + ApiKeys.Hash(ctx.Request.Headers["X-Bot-Token"].ToString())[..12] + ":" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = botPerMinute, Window = TimeSpan.FromMinutes(1) }));
    // Opening the page and listing/reading chats costs no AI quota, so it gets a roomier limit than asking.
    o.AddPolicy("bot-read", ctx => RateLimitPartition.GetFixedWindowLimiter(
        "r:" + ApiKeys.Hash(ctx.Request.Headers["X-Bot-Token"].ToString())[..12] + ":" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
    // "Send this to support" costs no AI quota but fills the admin's inbox, so it is limited tightly: per link and address, or per signed-in user.
    o.AddPolicy("support", ctx =>
    {
        var botToken = ctx.Request.Headers["X-Bot-Token"].ToString();
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var who = botToken.Length > 0
            ? "s:" + ApiKeys.Hash(botToken)[..12] + ":" + ip
            : "u:" + (ctx.User.FindFirst(ClaimNames.Tenant)?.Value ?? "anon") + ":" + (ctx.User.FindFirst("sub")?.Value ?? ip);
        return RateLimitPartition.GetFixedWindowLimiter(who, _ => new FixedWindowRateLimiterOptions { PermitLimit = 6, Window = TimeSpan.FromMinutes(10) });
    });
    // Cost protection: chat/search each spend free AI quota, so one signed-in user cannot burn the daily allowance.
    var aiPerMinute = builder.Configuration.GetValue("Security:AiRequestsPerMinute", 15);
    o.AddPolicy("ai", ctx =>
    {
        // Callers using X-Api-Key are counted per key (they are not authenticated yet at this point); token callers per user.
        var apiKey = ctx.Request.Headers[ApiKeyAuthHandler.Header].ToString();
        var who = apiKey.Length > 0
            ? "k:" + ApiKeys.Hash(apiKey)[..16]
            : (ctx.User.FindFirst(ClaimNames.Tenant)?.Value ?? "anon") + ":" + (ctx.User.FindFirst("sub")?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        return RateLimitPartition.GetFixedWindowLimiter(who, _ => new FixedWindowRateLimiterOptions { PermitLimit = aiPerMinute, Window = TimeSpan.FromMinutes(1) });
    });
});

var app = builder.Build();

app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    app.Logger.LogError(ex, "Unhandled exception for {Path}", ctx.Request.Path);
    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await ctx.Response.WriteAsJsonAsync(new ProblemDetails
    {
        Status = 500,
        Title = "An unexpected error occurred.",
        Extensions = { ["traceId"] = ctx.TraceIdentifier }
    });
}));

// Outside Development, force HTTPS (HSTS + redirect). Turn off only if TLS is terminated in front of the app.
if (!app.Environment.IsDevelopment() && app.Configuration.GetValue("Security:RequireHttps", true))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Browser hardening. The UI is plain same-origin HTML/CSS/JS, so a strict CSP is possible: no inline script,
// no third-party origins. Swagger UI needs inline scripts, so it is excluded.
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    // Only the share-link page may be framed, and only by this same site (the admin live preview).
    var framable = ctx.Request.Path.Equals("/bot.html", StringComparison.OrdinalIgnoreCase);
    h["X-Frame-Options"] = framable ? "SAMEORIGIN" : "DENY";
    if (ctx.Request.Path.StartsWithSegments("/api")) h["Cache-Control"] = "no-store"; // tokens, keys and answers are never cached
    if (!ctx.Request.Path.StartsWithSegments("/swagger"))
        h["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
                                       "connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors " + (framable ? "'self'" : "'none'");
    await next();
});

// API documentation is for developers: off in production unless explicitly enabled.
if (app.Environment.IsDevelopment() || app.Configuration.GetValue("Swagger:Enabled", false))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseDefaultFiles();
// The UI ships with the API and changes on every deploy. "no-cache" = the browser may keep a copy but must ask the server (cheap 304) before using it,
// so nobody runs a mix of old and new scripts (an old bot.js next to a new admin page was a dead preview).
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" });
app.UseAuthentication();
app.UseRateLimiter();   // after authentication so per-user limits can see who is calling
app.UseAuthorization();

app.MapControllers();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready"),
    ResponseWriter = async (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), detail = e.Value.Description })
        });
    }
}).AllowAnonymous();

// Container/first-run convenience: create or upgrade the SQL Server schema at startup (waits for the database to come up).
// Off by default; production on a shared SQL Server should apply the reviewed idempotent migration script instead.
if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<FlowMind.Infrastructure.Database.FlowMindDbContext>();
    for (var attempt = 1; ; attempt++)
    {
        try { db.Database.Migrate(); app.Logger.LogInformation("Database schema is up to date."); break; }
        catch (Exception ex) when (attempt < 40)
        {
            app.Logger.LogWarning("Database not ready yet (attempt {Attempt}/40): {Message}", attempt, ex.GetBaseException().Message);
            Thread.Sleep(3000);
        }
    }
}

app.Run();

public partial class Program { } // for WebApplicationFactory in tests
