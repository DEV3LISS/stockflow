using Microsoft.OpenApi;

namespace Stockflow.Api.Setup;

public static class OpenApiSetup
{
    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Stockflow API",
                    Version = "v1",
                    Description = "Online shop backend: catalog, inventory with an append-only ledger, cart, checkout with 15-minute stock reservations (the last unit is sold exactly once), signed idempotent payment webhooks, reservation expiry.",
                };

                // Adds the "Authorize" button in Swagger UI: paste the access token from /auth/login.
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "Online shop backend: catalog, inventory with an append-only ledger, cart, checkout with 15-minute stock reservations (the last unit is sold exactly once), signed idempotent payment webhooks, reservation expiry.",
                };
                document.Security =
                [
                    new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] },
                ];
                return Task.CompletedTask;
            });
        });
        return services;
    }
}
