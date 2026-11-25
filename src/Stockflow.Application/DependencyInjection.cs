using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Stockflow.Application.Auth;
using Stockflow.Application.Carts;
using Stockflow.Application.Catalog;
using Stockflow.Application.Common;
using Stockflow.Application.Inventory;
using Stockflow.Application.Orders;
using Stockflow.Application.Payments;
using Stockflow.Application.Users;

namespace Stockflow.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<StockflowMetrics>(); // IMeterFactory is provided by the host

        services.AddScoped<AuthService>();
        services.AddScoped<UserService>();
        services.AddScoped<CatalogService>();
        services.AddScoped<InventoryService>();
        services.AddScoped<CartService>();
        services.AddScoped<OrderService>();
        services.AddScoped<ExpiryService>();
        services.AddScoped<PaymentService>();

        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, ServiceLifetime.Singleton);
        return services;
    }
}
