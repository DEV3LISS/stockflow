using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Stockflow.Infrastructure.Persistence;

/// <summary>
/// Used only by the "dotnet ef" tool to create migrations. The connection string is never opened
/// when adding a migration, so a placeholder is fine.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        DependencyInjection.ConfigureDbContext(options, "Host=localhost;Database=stockflow_design");
        return new AppDbContext(options.Options);
    }
}
