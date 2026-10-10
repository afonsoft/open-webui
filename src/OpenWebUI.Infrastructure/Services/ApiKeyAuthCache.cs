using Microsoft.Extensions.Caching.Hybrid;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Cache de curta duração da identidade resolvida por chave de API (<c>sk-*</c>).
/// Sem ele, cada request autenticada por chave criava um escopo DI e fazia duas
/// consultas ao SQLite (ApiKeys + Users) no middleware de autenticação.
/// </summary>
public static class ApiKeyAuthCache
{
    private const string Prefix = "apikey-auth:";

    /// <summary>TTL absoluto — limita o tempo que uma chave revogada continua válida.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    /// <summary>Chave de cache a partir do hash da API key.</summary>
    public static string CacheKey(string keyHash) => Prefix + keyHash;

    /// <summary>Remove entradas em cache para os hashes informados (rotação/revogação).</summary>
    public static async Task EvictAsync(HybridCache cache, IEnumerable<string> keyHashes)
    {
        foreach (var hash in keyHashes)
        {
            await cache.RemoveAsync(CacheKey(hash));
        }
    }
}
