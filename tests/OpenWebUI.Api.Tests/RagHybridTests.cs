using System.Net;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do ranking híbrido do RagService: vetor puro vs BM25+vetor
/// produzem rankings diferentes em corpus controlado, e o rerank
/// reordena por cobertura de termos.
/// </summary>
[TestFixture]
public class RagHybridTests
{
    private string _dbPath = null!;

    [SetUp]
    public void SetUp() =>
        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-hybrid-{Guid.NewGuid():N}.db");

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private async Task<AppDbContext> CreateContextAsync()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static RagService NewRag(AppDbContext db) =>
        new(db, new EmbeddingService(new StubHttpClientFactory(), new ConfigService(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()))),
            new ConfigService(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())), new StubHttpClientFactory());

    private sealed class StubHttpClientFactory(HttpMessageHandler? handler = null)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            handler is null ? new() : new(handler, disposeHandler: false);
    }

    /// <summary>Responde {"scores":[0.01,0.99]} a qualquer request.</summary>
    private sealed class RerankHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"scores\":[0.01,0.99]}"),
            });
    }

    /// <summary>Falha toda request (provider indisponível).</summary>
    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("mock indisponível");
    }

    private static EmbeddingChunk Chunk(
        string fileId, int index, string text, float[] vector) => new()
    {
        UserId = "u1",
        FileId = fileId,
        ChunkIndex = index,
        Text = text,
        EmbeddingJson = System.Text.Json.JsonSerializer.Serialize(vector),
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    };

    [Test]
    public async Task Hybrid_LigaEDesliga_ProduzRankingsDiferentes()
    {
        await using var db = await CreateContextAsync();
        // Vetores: chunk A quase idêntico à query; chunk B ortogonal.
        // Texto: só o chunk B contém os termos da query — BM25 deve vencê-lo.
        var queryVector = new float[] { 1f, 0f };
        db.EmbeddingChunks.AddRange(
            Chunk("f1", 0, "texto sem relação com os termos", [0.99f, 0.01f]),
            Chunk("f1", 1, "gato cachorro gato cachorro", [0f, 1f]));
        await db.SaveChangesAsync();
        var rag = NewRag(db);

        var vetorial = await rag.VectorRankAsync("u1", queryVector, null);
        Assert.That(vetorial[0].chunk.ChunkIndex, Is.EqualTo(0));

        // Peso 0 => score 100% BM25: o chunk com os termos deve subir ao topo.
        var cfg = RetrievalConfig.Default with { Hybrid = true, HybridWeight = 0 };
        var hibrido = await rag.HybridRankAsync("u1", "gato cachorro", queryVector, null, cfg);

        Assert.That(hibrido[0].chunk.ChunkIndex, Is.EqualTo(1));
        Assert.That(hibrido[0].chunk.ChunkIndex, Is.Not.EqualTo(vetorial[0].chunk.ChunkIndex));
    }

    [Test]
    public async Task Retrieve_HybridTrue_UsaRankingHibrido()
    {
        await using var db = await CreateContextAsync();
        db.EmbeddingChunks.AddRange(
            Chunk("f1", 0, "irrelevante", [0.99f, 0.01f]),
            Chunk("f1", 1, "zebra girafa zebra girafa", [0f, 1f]));
        await db.SaveChangesAsync();

        var config = new ConfigService(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        await config.SetAsync("retrieval.config",
            RetrievalConfig.Default with { Hybrid = true, HybridWeight = 0, TopK = 1 });

        var rag = new RagService(db,
            new StubEmbeddingService(), config, new StubHttpClientFactory());
        var context = await rag.RetrieveAsync("u1", "zebra girafa", null);

        Assert.That(context, Does.Contain("zebra girafa"));
        Assert.That(context, Does.Not.Contain("irrelevante"));
    }

    [Test]
    public async Task Rerank_ReordenaPorCoberturaDeTermos()
    {
        var a = Chunk("f1", 0, "alfa", [1f]);
        var b = Chunk("f1", 1, "alfa beta gama", [1f]);
        var ranked = new List<(EmbeddingChunk chunk, double score)>
        {
            (a, 0.9), (b, 0.8),
        };

        var reranked = RagService.Rerank("alfa beta gama", ranked);

        Assert.That(reranked[0].chunk.ChunkIndex, Is.EqualTo(1));
    }

    [Test]
    public async Task Rerank_Externo_ReordenaPorScoresDoProvider()
    {
        await using var db = await CreateContextAsync();
        // Embeddings distintos: cosseno(alfa)=1.0 > cosseno(beta)~0.99 garante
        // ranked=[alfa,beta] — os scores do provider são posicionais.
        db.EmbeddingChunks.AddRange(
            Chunk("f1", 0, "alfa", [1f, 0f]),
            Chunk("f1", 1, "beta", [0.9f, 0.1f]));
        await db.SaveChangesAsync();

        var config = new ConfigService(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        await config.SetAsync("retrieval.config", RetrievalConfig.Default with
        {
            Rerank = true,
            RerankEngine = "external",
            RerankExternalUrl = "http://rerank.test/rerank",
            TopK = 2,
        });

        // Handler fake: scores invertidos — o segundo doc vence.
        var rag = new RagService(db, new StubEmbeddingService(), config,
            new StubHttpClientFactory(new RerankHandler()));
        var context = await rag.RetrieveAsync("u1", "alfa beta", null);

        Assert.That(context, Is.Not.Null);
        Assert.That(context!.IndexOf("beta"), Is.LessThan(context.IndexOf("alfa")));
    }

    [Test]
    public async Task Rerank_ExternoIndisponivel_CaiNoRerankLocal()
    {
        await using var db = await CreateContextAsync();
        db.EmbeddingChunks.AddRange(
            Chunk("f1", 0, "delta", [1f, 0f]),
            Chunk("f1", 1, "alfa beta gama", [0.9f, 0.1f]));
        await db.SaveChangesAsync();

        var config = new ConfigService(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        await config.SetAsync("retrieval.config", RetrievalConfig.Default with
        {
            Rerank = true,
            RerankEngine = "external",
            RerankExternalUrl = "http://rerank.test/rerank",
            TopK = 2,
        });

        // Handler que falha sempre: provider fora → rerank local por cobertura.
        var rag = new RagService(db, new StubEmbeddingService(), config,
            new StubHttpClientFactory(new FailingHandler()));
        var context = await rag.RetrieveAsync("u1", "alfa beta gama", null);

        Assert.That(context, Is.Not.Null);
        // Rerank local: o chunk que cobre mais termos vem primeiro.
        Assert.That(context!.IndexOf("alfa beta gama"), Is.LessThan(context.IndexOf("delta")));
    }

    /// <summary>EmbeddingService que retorna vetor fixo (sem provider).</summary>
    private sealed class StubEmbeddingService : EmbeddingService
    {
        public StubEmbeddingService() : base(new StubHttpClientFactory(), null!) { }

        public override Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) =>
            Task.FromResult<float[]?>([1f, 0f]);
    }
}
