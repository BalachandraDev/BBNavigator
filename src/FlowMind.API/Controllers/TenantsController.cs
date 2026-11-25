using FlowMind.API.Tenants;
using FlowMind.Application.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowMind.API.Controllers;

/// <summary>
/// Platform administrator: see every company and manage its access. Nothing here exposes any company's documents or answers;
/// the admin sees counts, can read or reset the company's sign-in key, disable access, or delete the company with all its data.
/// </summary>
[ApiController]
[Route("api/v1/tenants")]
[Authorize(Policy = "PlatformAdmin")]
public class TenantsController : ControllerBase
{
    private readonly TenantAdminService _tenants;
    private readonly FlowMind.Infrastructure.Accounts.AccountService _accounts;
    private readonly FlowMind.Infrastructure.Accounts.AccountMailer _mailer;
    public TenantsController(TenantAdminService tenants, FlowMind.Infrastructure.Accounts.AccountService accounts, FlowMind.Infrastructure.Accounts.AccountMailer mailer)
    { _tenants = tenants; _accounts = accounts; _mailer = mailer; }

    [HttpGet]
    public async Task<IReadOnlyList<TenantRow>> List(CancellationToken ct) => await _tenants.ListAsync(ct);

    /// <summary>Create a company. The key can also be viewed later by the platform admin (Show login).</summary>
    [HttpPost]
    public Task<IActionResult> Create(CreateTenantRequest req, CancellationToken ct) => Run(async () =>
    {
        // Check the first admin's e-mail BEFORE creating anything, so a typo does not leave a company without its administrator.
        var adminEmail = FlowMind.Infrastructure.Accounts.AccountService.NormalizeEmail(req.AdminEmail);
        if (adminEmail.Length > 0 && !FlowMind.Infrastructure.Accounts.AccountService.IsValidEmail(adminEmail)) return Problem("Enter a valid e-mail address for the company admin.", statusCode: 400);
        if (adminEmail.Length > 0 && await _accounts.EmailExistsAsync(adminEmail, ct)) return Problem("Someone already has an account with that e-mail address.", statusCode: 409);
        var (t, key) = await _tenants.CreateAsync(req.Name, req.Slug, ct);
        if (adminEmail.Length == 0) return Created($"/api/v1/tenants/{t.Id}", new CreateTenantResponse(t.Id, t.Name, t.Slug, key));
        try
        {
            var (u, token) = await _accounts.InviteAsync(t.Id, adminEmail, string.IsNullOrWhiteSpace(req.AdminName) ? adminEmail.Split('@')[0] : req.AdminName, "TenantAdmin", null, ct);
            var link = _mailer.Link($"{Request.Scheme}://{Request.Host}", "invite", token);
            var sent = _mailer.Enabled && await _mailer.SendInviteAsync(u, t.Name, link, ct);
            return Created($"/api/v1/tenants/{t.Id}", new CreateTenantResponse(t.Id, t.Name, t.Slug, key, u.Email, sent ? null : link, sent));
        }
        catch (FlowMind.Infrastructure.Accounts.AccountException ex)
        {
            await _tenants.DeleteAsync(t.Id, t.Slug, ct);   // an e-mail that is already taken must not leave an empty company behind
            return Problem(ex.Message, statusCode: ex.Status);
        }
    });

    /// <summary>Company ID and key for sign-in. Audited. 409 if the key was never stored: use reset-key.</summary>
    [HttpGet("{id:guid}/key")]
    public Task<IActionResult> Reveal(Guid id, CancellationToken ct) => Run(async () => Ok(await _tenants.RevealAsync(id, ct)));

    /// <summary>New key. The old one stops working at once and company-admin sessions opened with it end.</summary>
    [HttpPost("{id:guid}/reset-key")]
    public Task<IActionResult> ResetKey(Guid id, CancellationToken ct) => Run(async () => Ok(await _tenants.ResetKeyAsync(id, ct)));

    [HttpPost("{id:guid}/disable")]
    public Task<IActionResult> Disable(Guid id, CancellationToken ct) => Run(async () => { await _tenants.SetActiveAsync(id, false, ct); return NoContent(); });

    [HttpPost("{id:guid}/enable")]
    public Task<IActionResult> Enable(Guid id, CancellationToken ct) => Run(async () => { await _tenants.SetActiveAsync(id, true, ct); return NoContent(); });

    /// <summary>Permanently delete the company and ALL its data. <c>confirm</c> must equal the Company ID.</summary>
    [HttpDelete("{id:guid}")]
    public Task<IActionResult> Delete(Guid id, [FromQuery] string? confirm, CancellationToken ct) =>
        Run(async () => { await _tenants.DeleteAsync(id, confirm, ct); return NoContent(); });

    private async Task<IActionResult> Run(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (TenantAdminException ex) { return Problem(ex.Message, statusCode: ex.Status); }
    }
}
