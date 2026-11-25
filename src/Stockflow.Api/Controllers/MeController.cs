using Microsoft.AspNetCore.Mvc;
using Stockflow.Application.Users;

namespace Stockflow.Api.Controllers;

/// <summary>The currently logged-in user. Available to every role (fallback policy: authenticated).</summary>
[ApiController]
[Route("api/v1/users/me")]
[Produces("application/json")]
public sealed class MeController(UserService users) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<UserDto> Get(CancellationToken ct) => users.GetCurrentAsync(ct);
}
