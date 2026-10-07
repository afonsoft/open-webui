using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Api.Hubs;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Notifications;

/// <summary>
/// <see cref="IChatRunNotifier"/> via Web Push
/// (SPEC-20261007-chat-notifications RF-004): entrega <c>run.completed</c>
/// às subscriptions do dono da run — o único caminho que alcança um navegador
/// totalmente fechado (service worker). O envio é pulado quando o usuário já
/// tem conexão SignalR ativa (a aba aberta recebe pelo hub, sem custo de
/// push service). Subscriptions mortas (404/410) são removidas (RFC 8030 §7.3).
/// O payload nunca carrega o conteúdo integral — só título do chat + trecho.
/// </summary>
public sealed class WebPushChatRunNotifier(
    AppDbContext db,
    IWebPushSender sender,
    ILogger<WebPushChatRunNotifier> logger) : IChatRunNotifier
{
    /// <inheritdoc />
    public async Task RunCompletedAsync(ChatRunFinished run, CancellationToken cancellationToken)
    {
        // Com aba aberta o hub já entrega run.completed — push só quando o
        // usuário está desconectado (aba fechada/background).
        if (ChatHub.ConnectedUserIds().Contains(run.UserId, StringComparer.Ordinal))
        {
            return;
        }

        var subs = await db.ChatPushSubscriptions
            .Where(s => s.UserId == run.UserId)
            .ToListAsync(cancellationToken);
        if (subs.Count == 0)
        {
            return;
        }

        var title = await db.Chats.AsNoTracking()
            .Where(c => c.Id == run.ChatId)
            .Select(c => c.Title)
            .FirstOrDefaultAsync(cancellationToken);

        var payload = JsonSerializer.Serialize(new
        {
            runId = run.RunId,
            chatId = run.ChatId,
            title,
            status = run.Status,
            snippet = Snippet(run.PartialContent),
            url = $"/c/{Uri.EscapeDataString(run.ChatId)}",
        });

        foreach (var sub in subs)
        {
            var result = await sender.SendAsync(
                sub.Endpoint, sub.P256dh, sub.Auth, payload, cancellationToken);
            if (result.Sent)
            {
                continue;
            }

            if (result.HttpStatus is 404 or 410)
            {
                logger.LogInformation(
                    "Subscription push morta removida: {Id} ({Status})", sub.Id, result.HttpStatus);
                db.ChatPushSubscriptions.Remove(sub);
            }
            else
            {
                logger.LogWarning(
                    "Envio web push falhou para subscription {Id}: {Error} ({Status})",
                    sub.Id, result.Error, result.HttpStatus);
            }
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static string? Snippet(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var trimmed = content.Trim();
        return trimmed.Length <= 160 ? trimmed : trimmed[..157] + "...";
    }
}
