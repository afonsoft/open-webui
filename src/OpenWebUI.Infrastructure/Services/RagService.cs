using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Store vetorial local (SQLite via EF) + retrieval por similaridade de
/// cosseno. Chunks vivem por usuário; deleção do arquivo remove os chunks.
/// Sem provider de embedding, RetrieveAsync retorna null e o chamador usa
/// o fallback de texto integral.
/// </summary>
public class RagService(AppDbContext db, EmbeddingService embeddings)
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
    /// o fallback de texto integral nesse caso.
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
        return await SearchAsync(userId, queryVector, fileIds, ct);
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
        var query = db.EmbeddingChunks.AsNoTracking()
            .Where(c => c.UserId == userId);
        if (fileIds is { Count: > 0 })
        {
            query = query.Where(c => fileIds.Contains(c.FileId));
        }

        var scored = new List<(EmbeddingChunk chunk, double score)>();
        await foreach (var chunk in query.AsAsyncEnumerable().WithCancellation(ct))
        {
            var vector = JsonSerializer.Deserialize<float[]>(chunk.EmbeddingJson);
            if (vector is { Length: > 0 })
            {
                scored.Add((chunk, Cosine(queryVector, vector)));
            }
        }

        if (scored.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder("Contexto recuperado por similaridade:");
        foreach (var (chunk, _) in scored
                     .OrderByDescending(s => s.score)
                     .Take(TopK))
        {
            if (sb.Length + chunk.Text.Length + 4 > ContextBudget)
            {
                break;
            }
            sb.Append("\n\n---\n").Append(chunk.Text);
        }
        return sb.ToString();
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
