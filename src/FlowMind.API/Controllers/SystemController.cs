using Microsoft.AspNetCore.Mvc;

namespace FlowMind.API.Controllers;

[ApiController]
[Route("api/v1/system")]
public class SystemController : ControllerBase
{
    /// <summary>Basic service info for the UI and integrators.</summary>
    [HttpGet("info")]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    public IActionResult Info() => Ok(new
    {
        name = "BBNavigator",
        serverTimeUtc = DateTime.UtcNow   // deliberately no version or environment name for anonymous callers
    });
}
