using Stockflow.Domain.Common;

namespace Stockflow.Domain.Carts;

/// <summary>
/// A customer's cart. It does NOT reserve stock — otherwise anyone (or a bot) could lock up inventory by filling carts.
/// Stock is reserved only at checkout, for a limited time.
/// </summary>
public sealed class Cart : Entity, IHasTimestamps
{
    public const int MaxLines = 50;
    public const int MaxQuantityPerLine = 20;

    private readonly List<CartItem> _items = [];

    private Cart() { }

    public Guid CustomerId { get; private set; }
    public IReadOnlyCollection<CartItem> Items => _items;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static Cart For(Guid customerId) => new() { CustomerId = customerId };

    /// <summary>Sets the quantity of a product (adds the line if missing; 0 removes it).</summary>
    public void SetQuantity(Guid productId, int quantity)
    {
        if (quantity is < 0 or > MaxQuantityPerLine)
        {
            throw new DomainException($"Quantity must be between 0 and {MaxQuantityPerLine}.");
        }

        var item = _items.SingleOrDefault(i => i.ProductId == productId);
        if (quantity == 0)
        {
            if (item is not null)
            {
                _items.Remove(item);
            }

            return;
        }

        if (item is null)
        {
            if (_items.Count >= MaxLines)
            {
                throw new DomainException($"A cart can hold at most {MaxLines} different products.");
            }

            _items.Add(new CartItem(Id, productId, quantity));
        }
        else
        {
            item.Quantity = quantity;
        }
    }

    public void Clear() => _items.Clear();
}

public sealed class CartItem
{
    private CartItem() { }

    internal CartItem(Guid cartId, Guid productId, int quantity) => (CartId, ProductId, Quantity) = (cartId, productId, quantity);

    public Guid CartId { get; private set; }
    public Guid ProductId { get; private set; }
    public int Quantity { get; internal set; }
}
