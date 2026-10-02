using Microsoft.EntityFrameworkCore;

namespace OpenWebUI.Infrastructure.Data;

/// <summary>
/// Contexto PostgreSQL — mesmo modelo do <see cref="AppDbContext"/>, tipo
/// distinto para o EF selecionar apenas as migrações geradas para Npgsql
/// (atributo <c>[DbContext]</c> de cada migration casa por tipo exato).
/// Registrado como <see cref="AppDbContext"/> no DI quando
/// <c>DATABASE_PROVIDER=postgresql</c>.
/// </summary>
public class PostgresAppDbContext(DbContextOptions<PostgresAppDbContext> options)
    : AppDbContext(options)
{
}

/// <summary>Factory de design-time para <c>dotnet ef -c PostgresAppDbContext</c>.
/// A connection string pode ser um placeholder — só o provider importa ao
/// scaffoldar migrações.</summary>
public class PostgresDbContextFactory
    : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<PostgresAppDbContext>
{
    /// <inheritdoc />
    public PostgresAppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Database=openwebui;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<PostgresAppDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new PostgresAppDbContext(options);
    }
}
