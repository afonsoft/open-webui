using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenWebUI.Domain;

using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Dispara webhooks de notificação (por usuário e globais) de forma best-effort:
/// falhas são logadas e nunca quebram o fluxo originário. Payload assinado com
/// HMAC-SHA256 no header <c>X-Webhook-Signature</c>.
/// </summary>
public class NotificationService(
    IHttpClientFactory httpClientFactory, AppDbContext db, ILogger<NotificationService> logger)
{
    /// <summary>Nome do header com a assinatura HMAC-SHA256 hex do corpo.</summary>
    public const string SignatureHeader = "X-Webhook-Signature";

    /// <summary>Nome do header com o nome do evento.</summary>
    public const string EventHeader = "X-Webhook-Event";

    /// <summary>Dispara os webhooks que escutam <paramref name="eventName"/> (fire-and-forget controlado).</summary>
    /// <param name="eventName">Evento (ex.: user.pending, automation.failed).</param>
    /// <param name="data">Dados do evento serializados em <c>data</c>.</param>
    /// <param name="userId">Usuário alvo opcional — webhooks do usuário além dos globais.</param>
    /// <param name="ct">Token de cancelamento.</param>
    public async Task DispatchAsync(string eventName, object data, string? userId = null, CancellationToken ct = default)
    {
        var webhooks = await db.NotificationWebhooks
            .Where(w => w.Enabled && (w.OwnerId == null || w.OwnerId == userId))
            .ToListAsync(ct);
        var matching = webhooks
            .Where(w => w.Events.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(eventName, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (matching.Count == 0)
        {
            return;
        }

        var payload = new { @event = eventName, data, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        var body = System.Text.Json.JsonSerializer.Serialize(payload);
        foreach (var webhook in matching)
        {
            _ = Task.Run(() => SendSafelyAsync(webhook, eventName, body), CancellationToken.None);
        }
    }

    /// <summary>Envia um payload a um webhook; nunca lança exceção.</summary>
    public async Task<int> SendSafelyAsync(NotificationWebhook webhook, string eventName, string body)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, webhook.Url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation(EventHeader, eventName);
            var signature = ComputeSignature(webhook.Secret, body);
            request.Headers.TryAddWithoutValidation(SignatureHeader, $"sha256={signature}");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await httpClientFactory.CreateClient().SendAsync(request, timeout.Token);
            return (int)response.StatusCode;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Webhook {Id} para {Url} falhou no evento {Event}", webhook.Id, webhook.Url, eventName);
            return 0;
        }
    }

    /// <summary>Assinatura HMAC-SHA256 hex do corpo com o segredo do webhook.</summary>
    public static string ComputeSignature(string secret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }
}
