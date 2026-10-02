using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Store vetorial local (SQLite via EF) + retrieval por similaridade de
/// cosseno. Chunks vivem por usuário; deleção do arquivo remove os chunks.
/// Sem provider de embedding, RetrieveAsync retorna null e o chamador usa
/// o fallback de texto integral.
/// </summary>
public class RagService(AppDbContext db, EmbeddingService embeddings, ConfigService config)
{
    /// <summary>Tamanho máximo do chunk em caracteres.</summary>
    public int ChunkSize { get; set; } = 1000;

    /// <summary>Sobreposição entre chunks consecutivos.</summary>
    public int ChunkOverlap { get; set; } = 100;

    /// <summary>Top-K padrão do retrieval.</summary>
    public int TopK { get; set; } = 5;

    /// <summary>Budget máximo de caracteres do contexto recuperado.</summary>
    public int ContextBudget { get; set; } = 2000;

    /// <summary>Divide um texto em chunks com sobreposição.</summary>
    /// <param name="text">Texto completo.</param>
    /// <param name="chunkSize">Tamanho máximo por chunk.</param>
    /// <param name="overlap">Sobreposição entre chunks.</param>
    public static IReadOnlyList<string> ChunkText(string text, int chunkSize = 1000, int overlap = 100)
    {
        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return chunks;
        }

        var step = Math.Max(1, chunkSize - overlap);
        for (var start = 0; start < text.Length; start += step)
        {
            var length = Math.Min(chunkSize, text.Length - start);
            chunks.Add(text.Substring(start, length));
            if (start + length >= text.Length)
            {
                break;
            }
        }
        return chunks;
    }

    /// <summary>
    /// (Re)indexa um arquivo: gera chunks, embedda cada um e persiste.
    /// Sem provider de embedding retorna false e nada é indexado
    /// (upload não quebra — fallback de texto integral cobre).
    /// </summary>
    /// <param name="file">Arquivo com ExtractedText preenchido.</param>
    /// <param name="ct">Cancelamento.</param>
    public async Task<bool> IndexFileAsync(FileEntry file, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(file.ExtractedText))
        {
            return false;
        }

        var chunks = ChunkText(file.ExtractedText, ChunkSize, ChunkOverlap);
        var vectors = new List<float[]>(chunks.Count);
        foreach (var chunk in chunks)
        {
            var vector = await embeddings.EmbedAsync(chunk, ct);
            if (vector is null)
            {
                return false;
            }
            vectors.Add(vector);
        }

        await RemoveFileChunksAsync(file.Id, ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (var i = 0; i < chunks.Count; i++)
        {
            db.EmbeddingChunks.Add(new EmbeddingChunk
            {
                UserId = file.UserId,
                FileId = file.Id,
                ChunkIndex = i,
                Text = chunks[i],
                EmbeddingJson = JsonSerializer.Serialize(vectors[i]),
                CreatedAt = now,
            });
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Remove todos os chunks de um arquivo.</summary>
    /// <param name="fileId">Arquivo origem.</param>
    /// <param name="ct">Cancelamento.</param>
    public async Task RemoveFileChunksAsync(string fileId, CancellationToken ct = default)
    {
        await db.EmbeddingChunks.Where(c => c.FileId == fileId)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Recupera os top-K chunks mais similares à query dentro do escopo de
    /// arquivos (null = todos os arquivos do usuário). Retorna null quando
    /// não há embedding disponível (query ou corpus) — chamador deve usar
    /// o fallback de texto integral nesse caso. Honra retrieval.config:
    /// top-k, híbrido BM25+vetorial (hybrid_weight) e reranking.
    /// </summary>
    /// <param name="userId">Usuário dono dos chunks.</param>
    /// <param name="query">Texto da pergunta.</param>
    /// <param name="fileIds">Arquivos escopo (null = todos do usuário).</param>
    /// <param name="ct">Cancelamento.</param>
    public async Task<string?> RetrieveAsync(
        string userId, string query, IReadOnlyList<string>? fileIds, CancellationToken ct = default)
    {
        var queryVector = await embeddings.EmbedAsync(query, ct);
        if (queryVector is null)
        {
            return null;
        }

        var cfg = await config.GetAsync("retrieval.config", RetrievalConfig.Default, ct);
        var ranked = cfg.Hybrid
            ? await HybridRankAsync(userId, query, queryVector, fileIds, cfg, ct)
            : await VectorRankAsync(userId, queryVector, fileIds, ct);

        if (ranked.Count == 0)
        {
            return null;
        }

        if (cfg.Rerank)
        {
            ranked = Rerank(query, ranked);
        }

        return BuildContext(ranked, cfg.TopK);
    }

    /// <summary>
    /// Busca vetorial pura (testável sem provider): cosseno sobre os chunks
    /// do usuário no escopo, top-K e budget de caracteres.
    /// </summary>
    /// <param name="userId">Usuário dono.</param>
    /// <param name="queryVector">Vetor da query.</param>
    /// <param name="fileIds">Arquivos escopo (null = todos).</param>
    /// <param name="ct">Cancelamento.</param>
    public async Task<string?> SearchAsync(
        string userId, float[] queryVector, IReadOnlyList<string>? fileIds,
        CancellationToken ct = default)
    {
        var ranked = await VectorRankAsync(userId, queryVector, fileIds, ct);
        return ranked.Count == 0 ? null : BuildContext(ranked, TopK);
    }

    /// <summary>Chunks ranqueados por similaridade de cosseno (desc).</summary>
    public async Task<List<(EmbeddingChunk chunk, double score)>> VectorRankAsync(
        string userId, float[] queryVector, IReadOnlyList<string>? fileIds,
        CancellationToken ct = default)
    {
        var scored = new List<(EmbeddingChunk chunk, double score)>();
        await foreach (var chunk in ScopedChunks(userId, fileIds).WithCancellation(ct))
        {
            var vector = JsonSerializer.Deserialize<float[]>(chunk.EmbeddingJson);
            if (vector is { Length: > 0 })
            {
                scored.Add((chunk, Cosine(queryVector, vector)));
            }
        }
        return scored.OrderByDescending(s => s.score).ToList();
    }

    /// <summary>
    /// Ranking híbrido: score = weight * cosseno + (1-weight) * BM25
    /// normalizado sobre o mesmo corpus em memória.
    /// </summary>
    public async Task<List<(EmbeddingChunk chunk, double score)>> HybridRankAsync(
        string userId, string query, float[] queryVector,
        IReadOnlyList<string>? fileIds, RetrievalConfig cfg, CancellationToken ct = default)
    {
        var corpus = new List<(EmbeddingChunk chunk, float[] vector)>();
        await foreach (var chunk in ScopedChunks(userId, fileIds).WithCancellation(ct))
        {
            var vector = JsonSerializer.Deserialize<float[]>(chunk.EmbeddingJson);
            if (vector is { Length: > 0 })
            {
                corpus.Add((chunk, vector));
            }
        }
        if (corpus.Count == 0)
        {
            return [];
        }

        var terms = Tokenize(query);
        var bm25 = Bm25Scores(terms, corpus.Select(c => c.chunk.Text).ToList());
        var weight = Math.Clamp(cfg.HybridWeight, 0, 1);
        var scored = new List<(EmbeddingChunk chunk, double score)>(corpus.Count);
        for (var i = 0; i < corpus.Count; i++)
        {
            scored.Add((corpus[i].chunk,
                weight * Cosine(queryVector, corpus[i].vector) + (1 - weight) * bm25[i]));
        }
        return scored.OrderByDescending(s => s.score).ToList();
    }

    /// <summary>
    /// Reranking simples e determinístico: dentro do top-2K, boost por
    /// cobertura de termos exatos da query no texto do chunk.
    /// </summary>
    public static List<(EmbeddingChunk chunk, double score)> Rerank(
        string query, List<(EmbeddingChunk chunk, double score)> ranked)
    {
        var terms = Tokenize(query);
        if (terms.Count == 0)
        {
            return ranked;
        }

        return ranked
            .Select(s =>
            {
                var text = Tokenize(s.chunk.Text);
                var coverage = terms.Count(t => text.Contains(t)) / (double)terms.Count;
                return (s.chunk, score: s.score + coverage);
            })
            .OrderByDescending(s => s.score)
            .ToList();
    }

    private IAsyncEnumerable<EmbeddingChunk> ScopedChunks(
        string userId, IReadOnlyList<string>? fileIds)
    {
        var query = db.EmbeddingChunks.AsNoTracking().Where(c => c.UserId == userId);
        if (fileIds is { Count: > 0 })
        {
            query = query.Where(c => fileIds.Contains(c.FileId));
        }
        return query.AsAsyncEnumerable();
    }

    private string BuildContext(List<(EmbeddingChunk chunk, double score)> ranked, int topK)
    {
        var sb = new StringBuilder("Contexto recuperado por similaridade:");
        foreach (var (chunk, _) in ranked.Take(topK))
        {
            if (sb.Length + chunk.Text.Length + 4 > ContextBudget)
            {
                break;
            }
            sb.Append("\n\n---\n").Append(chunk.Text);
        }
        return sb.ToString();
    }

    private static List<string> Tokenize(string text) => text
        .ToLowerInvariant()
        .Split([' ', '\t', '\n', '\r', ',', '.', ';', ':', '?', '!'], StringSplitOptions.RemoveEmptyEntries)
        .Distinct()
        .ToList();

    /// <summary>BM25 (k1=1.5, b=0.75) normalizado para [0,1] pelo score máximo.</summary>
    internal static List<double> Bm25Scores(List<string> terms, List<string> documents)
    {
        var scores = new double[documents.Count];
        if (terms.Count == 0 || documents.Count == 0)
        {
            return scores.ToList();
        }

        var docTerms = documents.Select(Tokenize).ToList();
        var avgdl = docTerms.Average(d => (double)d.Count);
        var k1 = 1.5;
        var b = 0.75;

        foreach (var term in terms)
        {
            var df = docTerms.Count(d => d.Contains(term));
            if (df == 0)
            {
                continue;
            }
            var idf = Math.Log(1 + (documents.Count - df + 0.5) / (df + 0.5));
            for (var i = 0; i < documents.Count; i++)
            {
                var tf = docTerms[i].Count(t => t == term);
                if (tf == 0)
                {
                    continue;
                }
                var dl = docTerms[i].Count;
                scores[i] += idf * (tf * (k1 + 1)) / (tf + k1 * (1 - b + b * dl / avgdl));
            }
        }

        var max = scores.Max();
        return max <= 0 ? scores.ToList() : scores.Select(s => s / max).ToList();
    }

    /// <summary>Similaridade de cosseno entre dois vetores.</summary>
    internal static double Cosine(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < n; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
