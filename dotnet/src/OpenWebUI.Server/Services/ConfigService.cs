using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Server.Data;
using OpenWebUI.Shared.Contracts;

namespace OpenWebUI.Server.Services;

/// <summary>Armazena e recupera configurações persistidas no banco (tabela chave-valor).</summary>
public class ConfigService(AppDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Obtém uma configuração desserializada ou o valor padrão.</summary>
    /// <typeparam name="T">Tipo do valor.</typeparam>
    /// <param name="key">Chave da configuração.</param>
    /// <param name="defaultValue">Valor usado quando a chave não existe.</param>
    /// <param name="ct">Token de cancelamento.</param>
    public async Task<T> GetAsync<T>(string key, T defaultValue, CancellationToken ct = default)
    {
        var entry = await db.ConfigEntries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Key == key, ct);
        if (entry is null)
        {
            return defaultValue;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(entry.ValueJson, JsonOptions) ?? defaultValue;
        }
        catch (JsonException)
        {
            return defaultValue;
        }
    }

    /// <summary>Grava uma configuração serializada em JSON.</summary>
    /// <typeparam name="T">Tipo do valor.</typeparam>
    /// <param name="key">Chave da configuração.</param>
    /// <param name="value">Valor a persistir.</param>
    /// <param name="ct">Token de cancelamento.</param>
    public async Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        var entry = await db.ConfigEntries.FirstOrDefaultAsync(e => e.Key == key, ct);
        if (entry is null)
        {
            db.ConfigEntries.Add(new ConfigEntry
            {
                Key = key,
                ValueJson = json,
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
        }
        else
        {
            entry.ValueJson = json;
            entry.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Obtém a configuração de conexões com provedores de IA.</summary>
    /// <param name="ct">Token de cancelamento.</param>
    public Task<ConnectionsConfig> GetConnectionsAsync(CancellationToken ct = default) =>
        GetAsync("connections", ConnectionsConfig.Default, ct);

    /// <summary>Obtém (ou gera e persiste) o segredo usado para assinar tokens JWT.</summary>
    /// <param name="ct">Token de cancelamento.</param>
    public async Task<string> GetOrCreateJwtSecretAsync(CancellationToken ct = default)
    {
        var secret = await GetAsync<string?>("webui.jwt.secret", null, ct);
        if (string.IsNullOrEmpty(secret))
        {
            secret = Convert.ToBase64String(RandomNumberGeneratorBytes(64));
            await SetAsync("webui.jwt.secret", secret, ct);
        }

        return secret;
    }

    private static byte[] RandomNumberGeneratorBytes(int length)
    {
        var bytes = new byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return bytes;
    }
}
