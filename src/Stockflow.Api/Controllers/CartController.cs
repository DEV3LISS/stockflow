using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stockflow.Api.Setup;
using Stockflow.Application.Carts;

namespace Stockflow.Api.Controllers;

/// <summary>
/// The current customer's cart. There is no cart id in the URL on purpose: a customer cannot even address
/// someone else's cart (IDOR is impossible by design rather than prevented by a check).
/// </summary>
[ApiController]
[Route("api/v1/cart")]
[Authorize(Policy = Policies.CustomerOnly)]
[Produces("application/json")]
public sealed class CartController(CartService carts) : ControllerBase
{
    [HttpGet]
    public Task<CartDto> Get(CancellationToken ct) => carts.GetAsync(ct);

    /// <summary>Set the quantity of a product (0 removes it). Does not reserve stock.</summary>
    [HttpPut("items")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<CartDto> SetItem(SetCartItemRequest request, CancellationToken ct) => carts.SetItemAsync(request, ct);

    [HttpDelete]
    public Task<CartDto> Clear(CancellationToken ct) => carts.ClearAsync(ct);
}
