namespace Stockflow.Application.Common;

// Application-level errors, mapped to HTTP status codes in one place (Api: GlobalExceptionHandler).

/// <summary>HTTP 404. Also used instead of 403 when revealing that a resource exists would leak information
/// (another customer's order).</summary>
public sealed class NotFoundException(string entity, object key)
    : Exception($"{entity} '{key}' was not found.");

/// <summary>HTTP 409.</summary>
public class ConflictException(string message) : Exception(message);

/// <summary>HTTP 409 with the list of products that ran out.</summary>
public sealed class InsufficientStockException(IReadOnlyList<string> skus)
    : ConflictException($"Not enough stock for: {string.Join(", ", skus)}.")
{
    public IReadOnlyList<string> Skus { get; } = skus;
}

/// <summary>HTTP 401.</summary>
public sealed class UnauthorizedException(string message) : Exception(message);

/// <summary>HTTP 403.</summary>
public sealed class ForbiddenException(string message) : Exception(message);
