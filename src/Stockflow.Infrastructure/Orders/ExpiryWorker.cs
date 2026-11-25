using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stockflow.Application.Orders;

namespace Stockflow.Infrastructure.Orders;

public sealed class ExpiryOptions
{
    public const string SectionName = "Expiry";

    public bool Enabled { get; init; } = true;
    public int IntervalSeconds { get; init; } = 10;
}

/// <summary>Background loop that returns unpaid reservations to stock. Safe to run on every instance (SKIP LOCKED).</summary>
public sealed partial class ExpiryWorker(IServiceScopeFactory scopes, IOptions<ExpiryOptions> options, ILogger<ExpiryWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.IntervalSeconds));
        do
        {
            try
            {
                int expired;
                do
                {
                    await using var scope = scopes.CreateAsyncScope();
                    expired = await scope.ServiceProvider.GetRequiredService<ExpiryService>().ExpireDueAsync(stoppingToken);
                    if (expired > 0)
                    {
                        LogExpired(logger, expired);
                    }
                }
                while (expired == ExpiryService.BatchSize && !stoppingToken.IsCancellationRequested);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex); // database blip: try again on the next tick, never crash the host
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Expired {Count} unpaid orders and released their stock")]
    private static partial void LogExpired(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Expiry worker iteration failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
