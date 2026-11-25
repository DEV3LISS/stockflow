using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Stockflow.Domain.Auth;
using Stockflow.Domain.Carts;
using Stockflow.Domain.Catalog;
using Stockflow.Domain.Inventory;
using Stockflow.Domain.Orders;
using Stockflow.Domain.Payments;
using Stockflow.Domain.Users;

namespace Stockflow.Application.Abstractions;

/// <summary>What the use cases need from the database (implemented by the EF Core DbContext).</summary>
public interface IAppDbContext
{
    DbSet<User> Users { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<Category> Categories { get; }
    DbSet<Product> Products { get; }
    DbSet<StockItem> StockItems { get; }
    DbSet<InventoryMovement> InventoryMovements { get; }
    DbSet<Cart> Carts { get; }
    DbSet<Order> Orders { get; }
    DbSet<Payment> Payments { get; }
    DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads the order with its lines and locks the row (SELECT … FOR UPDATE) until the transaction ends.</summary>
    Task<Order?> LockOrderAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks up to <paramref name="batchSize"/> orders whose reservation expired, skipping rows other workers
    /// have already locked (FOR UPDATE SKIP LOCKED) — safe with several instances.
    /// </summary>
    Task<IReadOnlyList<Order>> LockExpiredOrdersAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken = default);
}

public sealed record StockLine(Guid ProductId, int Quantity);

/// <summary>
/// Set-based stock operations used at checkout, payment and expiry. Must be called inside a transaction.
/// This is THE contended operation of the shop (flash sale: hundreds of buyers, one row), so it is implemented
/// as an atomic conditional UPDATE in the database — see ADR-004 for the alternatives that were measured.
/// </summary>
public interface IStockReservations
{
    /// <returns>Products that did not have enough available stock (empty = everything reserved).</returns>
    Task<IReadOnlyList<Guid>> TryReserveAsync(IReadOnlyList<StockLine> lines, CancellationToken ct);

    /// <summary>Unpaid order cancelled/expired: reserved −= q.</summary>
    Task ReleaseAsync(IReadOnlyList<StockLine> lines, CancellationToken ct);

    /// <summary>Order paid: the reserved units leave the warehouse — on_hand −= q, reserved −= q.</summary>
    Task CommitSaleAsync(IReadOnlyList<StockLine> lines, CancellationToken ct);

    /// <summary>Paid order cancelled by an admin: the units come back — on_hand += q.</summary>
    Task ReturnAsync(IReadOnlyList<StockLine> lines, CancellationToken ct);
}

public sealed record ProviderPayment(string ProviderPaymentId, string PaymentUrl);

/// <summary>The external payment provider (a simulator in this project).</summary>
public interface IPaymentProvider
{
    Task<ProviderPayment> CreatePaymentAsync(Guid orderId, decimal amount, CancellationToken ct);
}

public interface IPasswordHasher
{
    string Hash(string password);

    PasswordCheck Verify(string passwordHash, string password);
}

public enum PasswordCheck
{
    Failed,
    Success,
    SuccessRehashNeeded,
}

public interface ITokenService
{
    TimeSpan RefreshTokenLifetime { get; }

    AccessToken CreateAccessToken(User user);

    string GenerateRefreshToken();

    string HashRefreshToken(string refreshToken);
}

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>The user who sent the current request (from the JWT), or nobody.</summary>
public interface ICurrentUser
{
    Guid? Id { get; }

    UserRole? Role { get; }

    string? CorrelationId { get; }
}
