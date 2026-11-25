using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stockflow.Application.Abstractions;
using Stockflow.Infrastructure.Auth;
using Stockflow.Infrastructure.Inventory;
using Stockflow.Infrastructure.Orders;
using Stockflow.Infrastructure.Payments;
using Stockflow.Infrastructure.Persistence;
using Stockflow.Infrastructure.Persistence.Interceptors;

namespace Stockflow.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<TimestampsInterceptor>();
        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            ConfigureDbContext(options, connectionString);
            options.AddInterceptors(sp.GetRequiredService<TimestampsInterceptor>());
        });
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        services.AddOptions<JwtOptions>().BindConfiguration(JwtOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<DatabaseOptions>().BindConfiguration(DatabaseOptions.SectionName);
        services.AddOptions<SeedOptions>().BindConfiguration(SeedOptions.SectionName);
        services.AddOptions<PaymentOptions>().BindConfiguration(PaymentOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<InventoryOptions>().BindConfiguration(InventoryOptions.SectionName);
        services.AddOptions<ExpiryOptions>().BindConfiguration(ExpiryOptions.SectionName);

        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<IPaymentProvider, SimulatedPaymentProvider>();

        // Strategy chosen by configuration (Inventory:ReservationStrategy) — the load test compares all three.
        services.AddScoped<IStockReservations>(sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            return sp.GetRequiredService<IOptions<InventoryOptions>>().Value.ReservationStrategy switch
            {
                ReservationStrategy.Pessimistic => new PessimisticStockReservations(db),
                ReservationStrategy.Optimistic => new OptimisticStockReservations(db),
                _ => new AtomicStockReservations(db),
            };
        });

        services.AddHostedService<ExpiryWorker>();
        return services;
    }

    internal static void ConfigureDbContext(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention();
}
