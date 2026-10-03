using System.Net.Http.Json;
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
public class RagService(
    AppDbContext db, EmbeddingService embeddings, ConfigService config,
    IHttpClientFactory httpFactory)
{
    /// <summary>Tamanho máximo do chunk em caracteres.</summary>
    public int ChunkSize { get; set; } = 1000;

    /// <summary>Sobreposição entre chunks consecutivos.</summary>
    public int ChunkOverlap { get; set; } = 100;

    /// <summary>Top-K padrão do retrieval.</summary>
    public int TopK { get; set; } = 5;

    /// <summary>Budget máximo de caracteres do contexto recuperado.</summary>
    public int ContextBudget { get; set; } = 2000;

    /// <summary>Candidatos mantidos do ranking para o rerank/top-K final.</summary>
    private const int CandidateLimit = 256;

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

        for (var start = 0; start < text.Length;)
        {
            var windowEnd = Math.Min(start + chunkSize, text.Length);
            var end = windowEnd;
            if (windowEnd < text.Length)
            {
                // Prefere cortar no último limite de parágrafo/frase da metade
                // final da janela — chunks não quebram sentenças no meio.
                var boundary = LastBoundary(text, start + chunkSize / 2, windowEnd);
                if (boundary > 0)
                {
                    end = boundary;
                }
            }

            var chunk = text.Substring(start, end - start).Trim();
            if (chunk.Length > 0)
            {
                chunks.Add(chunk);
            }
            if (end >= text.Length)
            {
                break;
            }
            // Sempre avança: end ≥ start + chunkSize/2 garante progresso.
            start = Math.Max(end - overlap, start + 1);
        }
        return chunks;
    }

    /// <summary>
    /// Último limite de parágrafo (<c>\n</c>) ou fim de frase (<c>. ! ?</c>
    /// seguido de espaço) entre <paramref name="min"/> (exclusivo) e
    /// <paramref name="end"/> (exclusivo); 0 quando não há.
    /// </summary>
    private static int LastBoundary(string text, int min, int end)
    {
        for (var i = end - 1; i > min; i--)
        {
            if (text[i] == '\n')
            {
                return i + 1;
            }
            if ((text[i] == '.' || text[i] == '!' || text[i] == '?')
                && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))
            {
                return i + 1;
            }
        }
        return 0;
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

        // Batch: uma requisição por provider embeddando todos os chunks de
        // uma vez (Ollama /api/embed e OpenAI /embeddings aceitam input[]).
        // Se o provider não atender o batch, cai no modo sequencial.
        var vectors = await embeddings.EmbedBatchAsync(chunks, ct) is { } batch
            && batch.Length == chunks.Count
            ? batch.ToList()
            : null;
        if (vectors is null)
        {
            vectors = new List<float[]>(chunks.Count);
            foreach (var chunk in chunks)
            {
                var vector = await embeddings.EmbedAsync(chunk, ct);
                if (vector is null)
                {
                    return false;
                }
                vectors.Add(vector);
            }
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
                EmbeddingNorm = L2Norm(vectors[i]),
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
            ranked = cfg.RerankEngine == "external"
                ? await ExternalRerankAsync(query, ranked, cfg, ct)
                : Rerank(query, ranked);
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
        var queryNorm = L2Norm(queryVector);
        // Min-heap dos CandidateLimit melhores: corpora grandes não
        // materializam/ordenam a lista inteira.
        var top = new PriorityQueue<(EmbeddingChunk chunk, double score), double>();
        await foreach (var chunk in ScopedChunks(userId, fileIds).WithCancellation(ct))
        {
            var vector = JsonSerializer.Deserialize<float[]>(chunk.EmbeddingJson);
            if (vector is not { Length: > 0 })
            {
                continue;
            }
            var score = Cosine(queryVector, queryNorm, vector, chunk.EmbeddingNorm);
            if (top.Count < CandidateLimit)
            {
                top.Enqueue((chunk, score), score);
            }
            else if (score > top.Peek().score)
            {
                top.Dequeue();
                top.Enqueue((chunk, score), score);
            }
        }
        return top.UnorderedItems.Select(i => i.Element)
            .OrderByDescending(s => s.score).ToList();
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
        var queryNorm = L2Norm(queryVector);
        var top = new PriorityQueue<(EmbeddingChunk chunk, double score), double>();
        for (var i = 0; i < corpus.Count; i++)
        {
            var score = weight * Cosine(queryVector, queryNorm, corpus[i].vector, corpus[i].chunk.EmbeddingNorm)
                + (1 - weight) * bm25[i];
            if (top.Count < CandidateLimit)
            {
                top.Enqueue((corpus[i].chunk, score), score);
            }
            else if (score > top.Peek().score)
            {
                top.Dequeue();
                top.Enqueue((corpus[i].chunk, score), score);
            }
        }
        return top.UnorderedItems.Select(i => i.Element)
            .OrderByDescending(s => s.score).ToList();
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
                var text = Tokenize(s.chunk.Text).ToHashSet(StringComparer.Ordinal);
                var coverage = terms.Count(text.Contains) / (double)terms.Count;
                return (s.chunk, score: s.score + coverage);
            })
            .OrderByDescending(s => s.score)
            .ToList();
    }

    /// <summary>
    /// Rerank por provider externo: POST {query, documents[]} no endpoint
    /// configurado esperando {scores[]} alinhados aos documentos. Qualquer
    /// falha (URL ausente, HTTP, payload inesperado) cai no rerank local.
    /// </summary>
    private async Task<List<(EmbeddingChunk chunk, double score)>> ExternalRerankAsync(
        string query, List<(EmbeddingChunk chunk, double score)> ranked,
        RetrievalConfig cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.RerankExternalUrl))
        {
            return Rerank(query, ranked);
        }

        try
        {
            var client = httpFactory.CreateClient(nameof(RagService));
            client.Timeout = TimeSpan.FromSeconds(15);
            using var request = new HttpRequestMessage(HttpMethod.Post, cfg.RerankExternalUrl);
            if (!string.IsNullOrEmpty(cfg.RerankExternalApiKey))
            {
                request.Headers.TryAddWithoutValidation(
                    "Authorization", $"Bearer {cfg.RerankExternalApiKey}");
            }
            request.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    query,
                    documents = ranked.Select(r => r.chunk.Text).ToArray(),
                }, JsonSerializerOptions.Web),
                Encoding.UTF8, "application/json");
            var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var doc = await response.Content.ReadFromJsonAsync<JsonElement>(
                JsonSerializerOptions.Web, ct);

            if (doc.TryGetProperty("scores", out var scores)
                && scores.GetArrayLength() == ranked.Count)
            {
                return ranked
                    .Select((r, i) => (r.chunk, score: scores[i].GetDouble()))
                    .OrderByDescending(p => p.score)
                    .ToList();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // fallback para o rerank local em qualquer falha do provider
        }

        return Rerank(query, ranked);
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

        // HashSet por documento — evita re-contar a lista de termos a cada
        // termo da query (era O(termos × docs × tokens)). Mantém a semântica
        // original: tf binário (termo presente/ausente) e dl = nº de termos
        // distintos, pois Tokenize() já faz Distinct().
        var docTerms = documents
            .Select(d => Tokenize(d).ToHashSet(StringComparer.Ordinal))
            .ToList();
        var docLengths = docTerms.Select(d => (double)d.Count).ToList();
        var avgdl = docLengths.Average();
        var k1 = 1.5;
        var b = 0.75;
        const int tf = 1;

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
                if (!docTerms[i].Contains(term))
                {
                    continue;
                }
                var dl = docLengths[i];
                scores[i] += idf * (tf * (k1 + 1)) / (tf + k1 * (1 - b + b * dl / avgdl));
            }
        }

        var max = scores.Max();
        return max <= 0 ? scores.ToList() : scores.Select(s => s / max).ToList();
    }

    /// <summary>Similaridade de cosseno entre dois vetores.</summary>
    internal static double Cosine(float[] a, float[] b)
        => Cosine(a, L2Norm(a), b, 0);

    /// <summary>
    /// Cosseno com normas pré-computadas: <paramref name="normB"/> ≤ 0
    /// (chunks anteriores à coluna EmbeddingNorm) recalcula em memória.
    /// </summary>
    internal static double Cosine(float[] a, double normA, float[] b, double normB)
    {
        var n = Math.Min(a.Length, b.Length);
        double dot = 0;
        for (var i = 0; i < n; i++)
        {
            dot += a[i] * b[i];
        }
        var nb = normB > 0 ? normB : L2Norm(b);
        return normA == 0 || nb == 0 ? 0 : dot / (normA * nb);
    }

    /// <summary>Norma L2 do vetor.</summary>
    internal static double L2Norm(float[] v)
    {
        double sum = 0;
        foreach (var x in v)
        {
            sum += x * x;
        }
        return Math.Sqrt(sum);
    }
}
