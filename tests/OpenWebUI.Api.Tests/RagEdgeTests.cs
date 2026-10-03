using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>Branches de erro e borda do RagService (chunking, search e retrieval) e do DatabaseMigrator.</summary>
[TestFixture]
public class RagEdgeTests
{
    private string _dbPath = null!;

    [SetUp]
    public void SetUp() =>
        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-rag-{Guid.NewGuid():N}.db");

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

    /// <summary>Cria o contexto com o schema completo (necessário para os testes de RAG).</summary>
    private async Task<AppDbContext> CreateContextAsync()
    {
        var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private RagService NewRag(AppDbContext db) =>
        new(db, new EmbeddingService(new StubHttpClientFactory(), new ConfigService(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()))), new ConfigService(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())), new StubHttpClientFactory());

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static FileEntry NewFile(string? text) => new()
    {
        UserId = "u1",
        Filename = "doc.txt",
        ExtractedText = text,
    };

    private static EmbeddingChunk NewChunk(string fileId, string text, string embeddingJson) =>
        new()
        {
            UserId = "u1",
            FileId = fileId,
            ChunkIndex = 0,
            Text = text,
            EmbeddingJson = embeddingJson,
            CreatedAt = 1,
        };

    // ---------- RagService ----------

    [Test]
    public void Chunk_TextoVazioOuEspacos_RetornaVazio()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RagService.ChunkText(""), Is.Empty);
            Assert.That(RagService.ChunkText("   "), Is.Empty);
        });
    }

    [Test]
    public void Chunk_ComOverlap_RespeitaTamanhoESobreposicao()
    {
        var text = new string('a', 2500);
        var chunks = RagService.ChunkText(text, chunkSize: 1000, overlap: 100);

        Assert.Multiple(() =>
        {
            Assert.That(chunks, Has.Count.EqualTo(3));
            Assert.That(chunks[0], Has.Length.EqualTo(1000));
            // step = 900: chunks em 0, 900 e 1800 — o último cobre os 700 restantes.
            Assert.That(chunks[2], Has.Length.EqualTo(700));
            Assert.That(chunks[1][0], Is.EqualTo(text[900]));
        });
    }

    [Test]
    public async Task Index_ArquivoVazioOuBinario_RetornaFalse()
    {
        await using var db = await CreateContextAsync();
        var rag = NewRag(db);

        // Arquivo binário não tem ExtractedText extraído.
        Assert.Multiple(async () =>
        {
            Assert.That(await rag.IndexFileAsync(NewFile(null)), Is.False);
            Assert.That(await rag.IndexFileAsync(NewFile("   ")), Is.False);
        });
        Assert.That(await db.EmbeddingChunks.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task Index_SemProviderDeEmbedding_RetornaFalse()
    {
        await using var db = await CreateContextAsync();
        var rag = NewRag(db);

        // Sem conexões configuradas, EmbedAsync devolve null e nada é indexado.
        var indexed = await rag.IndexFileAsync(NewFile("conteudo de texto suficiente"));
        Assert.Multiple(async () =>
        {
            Assert.That(indexed, Is.False);
            Assert.That(await db.EmbeddingChunks.CountAsync(), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Search_ColecaoVazia_RetornaNull()
    {
        await using var db = await CreateContextAsync();
        var rag = NewRag(db);

        var result = await rag.SearchAsync("u1", [1f, 0f], null);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Search_EmbeddingVazio_IgnoraChunkERetornaNull()
    {
        await using var db = await CreateContextAsync();
        var rag = NewRag(db);
        db.EmbeddingChunks.Add(NewChunk("f1", "texto", "[]"));
        await db.SaveChangesAsync();

        var result = await rag.SearchAsync("u1", [1f, 0f], null);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Search_EscopoOutroArquivo_RetornaNull()
    {
        await using var db = await CreateContextAsync();
        var rag = NewRag(db);
        db.EmbeddingChunks.Add(NewChunk("f1", "texto", "[1.0,0.0]"));
        await db.SaveChangesAsync();

        var result = await rag.SearchAsync("u1", [1f, 0f], ["f2"]);
        Assert.That(result, Is.Null);

        // Escopo correto retorna o contexto montado.
        var hit = await rag.SearchAsync("u1", [1f, 0f], ["f1"]);
        Assert.That(hit, Does.Contain("Contexto recuperado").And.Contain("texto"));
    }

    [Test]
    public async Task Search_OrdenaPorSimilaridadeERespeitaBudget()
    {
        await using var db = await CreateContextAsync();
        var rag = NewRag(db);
        // Budget apertado: cabe o chunk mais similar mas o segundo estoura.
        rag.ContextBudget = 120;
        db.EmbeddingChunks.Add(NewChunk("f1", "chunk distante " + new string('f', 60), "[0.0,1.0]"));
        db.EmbeddingChunks.Add(NewChunk("f1", "chunk proximo " + new string('p', 60), "[1.0,0.0]"));
        await db.SaveChangesAsync();

        // Query alinhada ao segundo chunk: ele entra primeiro; o budget
        // pequeno corta o restante (branch do break no budget).
        var result = await rag.SearchAsync("u1", [1f, 0f], null);
        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Contain("chunk proximo"));
            Assert.That(result, Does.Not.Contain("chunk distante"));
        });
    }

    [Test]
    public async Task Retrieve_SemProvider_RetornaNull()
    {
        await using var db = await CreateContextAsync();
        var rag = NewRag(db);
        db.EmbeddingChunks.Add(NewChunk("f1", "texto", "[1.0,0.0]"));
        await db.SaveChangesAsync();

        // Sem provider, nem a query vira embedding: retrieval devolve null
        // e o chamador cai no fallback de texto integral.
        var result = await rag.RetrieveAsync("u1", "pergunta", null);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task RemoveFileChunks_RemoveSomenteDoArquivo()
    {
        await using var db = await CreateContextAsync();
        var rag = NewRag(db);
        db.EmbeddingChunks.Add(NewChunk("f1", "a", "[1.0]"));
        db.EmbeddingChunks.Add(NewChunk("f2", "b", "[1.0]"));
        await db.SaveChangesAsync();

        await rag.RemoveFileChunksAsync("f1");

        Assert.Multiple(async () =>
        {
            Assert.That(await db.EmbeddingChunks.CountAsync(), Is.EqualTo(1));
            Assert.That(await db.EmbeddingChunks.SingleAsync(), Is.Not.Null);
        });
    }

    // ---------- DatabaseMigrator ----------

    [Test]
    public async Task Migrate_DbJaNaVersaoAtual_IdempotenteSemDuplicarHistorico()
    {
        await using var db = CreateContext();
        await DatabaseMigrator.MigrateAsync(db);
        await DatabaseMigrator.MigrateAsync(db);

        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Multiple(async () =>
        {
            Assert.That(applied.Count(), Is.EqualTo(db.Database.GetMigrations().Count()));
            Assert.That(await db.Database.GetPendingMigrationsAsync(), Is.Empty);
        });
    }

    [Test]
    public async Task Migrate_DbMigradoComDados_PreservaRegistros()
    {
        await using (var db = CreateContext())
        {
            await DatabaseMigrator.MigrateAsync(db);
            db.Users.Add(new User
            {
                Name = "Persistente",
                Email = "persistente@rag.local",
                PasswordHash = "x",
                Role = "user",
            });
            await db.SaveChangesAsync();
        }

        // Segunda execução no mesmo banco: nada a migrar e dados intactos.
        await using (var db = CreateContext())
        {
            await DatabaseMigrator.MigrateAsync(db);
            Assert.Multiple(async () =>
            {
                Assert.That(await db.Users.CountAsync(), Is.EqualTo(1));
                Assert.That(await db.Database.GetPendingMigrationsAsync(), Is.Empty);
            });
        }
    }

    [Test]
    public async Task Migrate_BaseLegada_MarcaTodasAsMigracoesComoAplicadas()
    {
        // EnsureCreated sobe o schema atual sem __EFMigrationsHistory:
        // é o caso do baseline de bases pré-migrations.
        await using (var legacy = CreateContext())
        {
            await legacy.Database.EnsureCreatedAsync();
            legacy.Users.Add(new User
            {
                Name = "Legado",
                Email = "legado@rag.local",
                PasswordHash = "x",
                Role = "admin",
            });
            await legacy.SaveChangesAsync();
        }

        await using var db = CreateContext();
        await DatabaseMigrator.MigrateAsync(db);

        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Multiple(async () =>
        {
            Assert.That(applied.Count(), Is.EqualTo(db.Database.GetMigrations().Count()));
            Assert.That(await db.Users.CountAsync(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Migrate_ArquivoVazioExistente_CriaSchemaCompleto()
    {
        // Arquivo criado vazio (sem nenhuma tabela): segue o caminho normal,
        // sem baseline, aplicando todas as migrações.
        await File.WriteAllBytesAsync(_dbPath, []);

        await using var db = CreateContext();
        await DatabaseMigrator.MigrateAsync(db);

        var tables = new List<string>();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }
        await connection.CloseAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(tables, Does.Contain("Users"));
            Assert.That(tables, Does.Contain("__EFMigrationsHistory"));
            Assert.That(await db.Database.GetPendingMigrationsAsync(), Is.Empty);
        });
    }
}
