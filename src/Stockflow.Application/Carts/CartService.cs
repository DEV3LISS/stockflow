using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Stockflow.Application.Abstractions;
using Stockflow.Application.Common;
using Stockflow.Domain.Carts;
using Stockflow.Domain.Common;

namespace Stockflow.Application.Carts;

public sealed record CartLineDto(Guid ProductId, string Sku, string Name, decimal UnitPrice, int Quantity, decimal LineTotal, int Available);

/// <summary>Prices and availability are current values for display — nothing is promised until checkout.</summary>
public sealed record CartDto(IReadOnlyList<CartLineDto> Lines, decimal Total, bool AllAvailable);

public sealed record SetCartItemRequest(Guid ProductId, int Quantity);

public sealed class SetCartItemRequestValidator : AbstractValidator<SetCartItemRequest>
{
    public SetCartItemRequestValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Quantity).InclusiveBetween(0, Cart.MaxQuantityPerLine);
    }
}

/// <summary>The current customer's cart. A customer can only ever reach their own cart (no cart id in the API).</summary>
public sealed class CartService(IAppDbContext db, ICurrentUser currentUser, IValidator<SetCartItemRequest> validator)
{
    public async Task<CartDto> GetAsync(CancellationToken ct)
    {
        var customerId = CustomerId();
        var lines = await (
            from c in db.Carts.AsNoTracking()
            from i in c.Items
            join p in db.Products on i.ProductId equals p.Id
            join s in db.StockItems on p.Id equals s.ProductId
            where c.CustomerId == customerId
            orderby p.Name
            select new CartLineDto(p.Id, p.Sku, p.Name, p.Price, i.Quantity, p.Price * i.Quantity, s.OnHand - s.Reserved))
            .ToListAsync(ct);

        return new CartDto(lines, lines.Sum(l => l.LineTotal), lines.All(l => l.Available >= l.Quantity));
    }

    public async Task<CartDto> SetItemAsync(SetCartItemRequest request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        if (request.Quantity > 0 && !await db.Products.AnyAsync(p => p.Id == request.ProductId && p.IsActive, ct))
        {
            throw new DomainException("This product is not available for sale.");
        }

        var cart = await LoadOrCreateAsync(ct);
        cart.SetQuantity(request.ProductId, request.Quantity);
        await db.SaveChangesAsync(ct);
        return await GetAsync(ct);
    }

    public async Task<CartDto> ClearAsync(CancellationToken ct)
    {
        var cart = await LoadOrCreateAsync(ct);
        cart.Clear();
        await db.SaveChangesAsync(ct);
        return await GetAsync(ct);
    }

    private async Task<Cart> LoadOrCreateAsync(CancellationToken ct)
    {
        var customerId = CustomerId();
        var cart = await db.Carts.Include(c => c.Items).SingleOrDefaultAsync(c => c.CustomerId == customerId, ct);
        if (cart is null)
        {
            cart = Cart.For(customerId);
            db.Carts.Add(cart);
        }

        return cart;
    }

    private Guid CustomerId() => currentUser.Id ?? throw new UnauthorizedException("Not authenticated.");
}
