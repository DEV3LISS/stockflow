using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stockflow.Api.Setup;
using Stockflow.Application.Common;
using Stockflow.Application.Orders;
using Stockflow.Application.Payments;
using Stockflow.Domain.Orders;

namespace Stockflow.Api.Controllers;

/// <summary>Checkout and orders. Customers see only their own orders; admins see all.</summary>
[ApiController]
[Route("api/v1/orders")]
[Produces("application/json")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
public sealed class OrdersController(OrderService orders, PaymentService payments) : ControllerBase
{
    /// <summary>
    /// Place an order from the cart and reserve all items for 15 minutes — all or nothing.
    /// Requires header <c>Idempotency-Key</c>: a retry with the same key returns the same order (200) instead of a new one (201).
    /// 409 with <c>outOfStock</c> lists the SKUs that ran out.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = Policies.CustomerOnly)]
    [ProducesResponseType<OrderDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<OrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OrderDto>> Place([FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        var result = await orders.PlaceAsync(idempotencyKey, ct);
        return result.Replayed ? Ok(result.Order) : CreatedAtAction(nameof(Get), new { id = result.Order.Id }, result.Order);
    }

    [HttpGet]
    public Task<PagedResult<OrderDto>> List([FromQuery] OrderStatus? status, [FromQuery] Guid? customerId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        orders.ListAsync(new OrderQuery(new PageRequest(page, pageSize), status, customerId), ct);

    /// <summary>404 also for other customers' orders (does not reveal that the id exists).</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<OrderDto> Get(Guid id, CancellationToken ct) => orders.GetAsync(id, ct);

    /// <summary>Start paying: returns the provider's payment page. Use the simulator endpoint to complete it.</summary>
    [HttpPost("{id:guid}/payments")]
    [Authorize(Policy = Policies.CustomerOnly)]
    [ProducesResponseType<StartPaymentResult>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<StartPaymentResult>> Pay(Guid id, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await payments.StartAsync(id, ct));

    /// <summary>Customer: unpaid own order (stock released). Admin: also paid orders (stock returned).</summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<OrderDto> Cancel(Guid id, CancellationToken ct) => orders.CancelAsync(id, ct);

    [HttpPost("{id:guid}/ship")]
    [Authorize(Policy = Policies.AdminOnly)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<OrderDto> Ship(Guid id, CancellationToken ct) => orders.ShipAsync(id, ct);
}
