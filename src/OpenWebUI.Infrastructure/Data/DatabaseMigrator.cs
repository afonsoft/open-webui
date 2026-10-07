using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace OpenWebUI.Infrastructure.Data;

/// <summary>
/// Aplica migrações EF Core no startup. Bases criadas pelo antigo
/// <c>SchemaBootstrap</c> (schema atualizado mas sem <c>__EFMigrationsHistory</c>)
/// são baselinadas: todas as migrações existentes são marcadas como aplicadas
/// sem recriar tabelas nem perder dados.
/// </summary>
public static class DatabaseMigrator
{
    private const string HistoryTable = "__EFMigrationsHistory";

    /// <summary>Aplica migrações pendentes, fazendo baseline em bases legadas.</summary>
    public static async Task MigrateAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            var isLegacy = TableExists(connection, "Users") && !TableExists(connection, HistoryTable);
            if (isLegacy)
            {
                Baseline(connection, db.Database.GetMigrations());
            }
        }
        finally
        {
            await connection.CloseAsync();
        }

        await db.Database.MigrateAsync(cancellationToken);

        // WAL: escritas concorrentes (runs salvando checkpoints enquanto
        // requests leem) não colidem com leitores — o pragma é persistente
        // no arquivo e no-op em bases ":memory:".
        await db.Database.ExecuteSqlRawAsync(
            "PRAGMA journal_mode=WAL", cancellationToken);
    }

    private static bool TableExists(System.Data.Common.DbConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = table;
        command.Parameters.Add(parameter);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static void Baseline(System.Data.Common.DbConnection connection, IEnumerable<string> migrations)
    {
        var productVersion = typeof(DbContext).Assembly.GetName().Version?.ToString(3) ?? "10.0.0";
        Execute(connection, $"""
            CREATE TABLE "{HistoryTable}" (
                "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL
            )
            """);
        foreach (var migration in migrations)
        {
            Execute(connection,
                $"INSERT INTO \"{HistoryTable}\" (\"MigrationId\",\"ProductVersion\") VALUES ('{migration}','{productVersion}')");
        }
    }

    private static void Execute(System.Data.Common.DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
