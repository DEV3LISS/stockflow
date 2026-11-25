using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Stockflow.Application.Common;
using Stockflow.Domain.Orders;
using Stockflow.Domain.Payments;
using Stockflow.Infrastructure.Persistence;

namespace Stockflow.Api.Setup;

/// <summary>
/// Three pillars: <b>logs</b> (Serilog, see Program.cs), <b>metrics</b> (Prometheus scrape at /metrics)
/// and <b>traces</b> (OTLP → Jaeger/Tempo/any vendor, only if an endpoint is configured).
/// </summary>
public static class Observability
{
    public static IServiceCollection AddObservability(this IServiceCollection services, IConfiguration configuration)
    {
        var otlpEndpoint = configuration["OpenTelemetry:OtlpEndpoint"];

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("stockflow"))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()   // http.server.request.duration by route and status → RED metrics
                .AddRuntimeInstrumentation()      // GC, thread pool, exceptions
                .AddMeter(StockflowMetrics.MeterName)
                .AddMeter("Npgsql")               // connection pool usage, command duration
                .AddPrometheusExporter())
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health")
                                                                         && !ctx.Request.Path.StartsWithSegments("/metrics"))
                    .AddSource("Npgsql"); // every SQL command as a child span: see which query made a request slow
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
                }
            });

        return services;
    }
}

/// <summary>
/// The expiry worker is "degraded" when unpaid orders stay reserved more than 5 minutes past their deadline:
/// the worker is not running (or failing), so stock is stuck and customers see "out of stock" for nothing.
/// The API itself still works, so this is Degraded (not part of readiness).
/// </summary>
public sealed class ReservationExpiryHealthCheck(AppDbContext db, TimeProvider clock) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var threshold = clock.GetUtcNow().AddMinutes(-5);
        var overdue = await db.Orders.CountAsync(o => o.Status == OrderStatus.PendingPayment && o.ReservedUntil < threshold, cancellationToken);
        var refunds = await db.Payments.CountAsync(p => p.Status == PaymentStatus.NeedsRefund, cancellationToken);

        var data = new Dictionary<string, object> { ["overdueReservations"] = overdue, ["paymentsNeedingRefund"] = refunds };
        return overdue > 0
            ? HealthCheckResult.Degraded($"{overdue} reservations are overdue — is the expiry worker running?", data: data)
            : HealthCheckResult.Healthy(data: data);
    }
}
