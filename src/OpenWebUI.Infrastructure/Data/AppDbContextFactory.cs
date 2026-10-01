using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpenWebUI.Infrastructure.Data;

/// <summary>
/// Factory de design-time para o <c>dotnet ef</c>: evita executar o startup do
/// Api (que roda <see cref="DatabaseMigrator"/>) ao scaffoldar migrações.
/// Connection string via <c>ConnectionStrings__Default</c> ou webui.db local.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <inheritdoc />
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Data Source=webui.db";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new AppDbContext(options);
    }
}
