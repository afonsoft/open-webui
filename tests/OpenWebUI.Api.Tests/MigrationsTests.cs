using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes do baseline de migrações EF Core (SPEC-20261001-ef-migrations).</summary>
[TestFixture]
public class MigrationsTests
{
    private string _dbPath = null!;

    [SetUp]
    public void SetUp() =>
        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-migrations-{Guid.NewGuid():N}.db");

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private static string[] Tables(AppDbContext db)
    {
        var tables = new List<string>();
        var connection = db.Database.GetDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }
        connection.Close();
        return [.. tables];
    }

    [Test]
    // Covers RF-001: base nova sobe com schema completo via migrations.
    public async Task Migrate_BancoNovo_CriaSchemaCompleto()
    {
        await using var db = CreateContext();
        await DatabaseMigrator.MigrateAsync(db);

        var tables = Tables(db);
        Assert.Multiple(() =>
        {
            Assert.That(tables, Does.Contain("Users"));
            Assert.That(tables, Does.Contain("Chats"));
            Assert.That(tables, Does.Contain("__EFMigrationsHistory"));
        });
    }

    [Test]
    // Covers RF-002: base legada (schema atual sem histórico) é baselinada e dados preservados.
    public async Task Migrate_BancoLegado_FazBaselineSemRecriar()
    {
        await using (var legacy = CreateContext())
        {
            await legacy.Database.EnsureCreatedAsync();
            legacy.Users.Add(new Domain.User
            {
                Id = "u1", Name = "Legado", Email = "legado@test.local",
                PasswordHash = "x", Role = "admin",
            });
            await legacy.SaveChangesAsync();
        }

        await using var db = CreateContext();
        await DatabaseMigrator.MigrateAsync(db);
        await DatabaseMigrator.MigrateAsync(db); // idempotente

        var tables = Tables(db);
        Assert.Multiple(async () =>
        {
            Assert.That(tables, Does.Contain("__EFMigrationsHistory"));
            Assert.That(await db.Users.CountAsync(), Is.EqualTo(1));
            Assert.That(await db.Database.GetPendingMigrationsAsync(), Is.Empty);
        });
    }
}
