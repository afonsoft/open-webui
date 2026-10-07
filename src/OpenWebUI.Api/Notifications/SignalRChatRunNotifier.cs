using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Api.Hubs;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Notifications;

/// <summary>
/// <see cref="IChatRunNotifier"/> via SignalR
/// (SPEC-20261007-chat-notifications RF-002): publica <c>run.completed</c>
/// para todas as conexões do dono da run (<see cref="Clients.User"/>), assim o
/// evento alcança até abas em outras telas — não só o grupo do chat aberto.
/// O payload traz título do chat e trecho para toast/Notification API.
/// </summary>
public sealed class SignalRChatRunNotifier(
    IHubContext<ChatHub> hubContext,
    AppDbContext db) : IChatRunNotifier
{
    /// <summary>Nome do evento enviado aos clientes conectados.</summary>
    public const string RunCompletedEvent = "run.completed";

    /// <inheritdoc />
    public async Task RunCompletedAsync(ChatRunFinished run, CancellationToken cancellationToken)
    {
        var title = await db.Chats.AsNoTracking()
            .Where(c => c.Id == run.ChatId)
            .Select(c => c.Title)
            .FirstOrDefaultAsync(cancellationToken);

        await hubContext.Clients.User(run.UserId).SendAsync(
            RunCompletedEvent,
            new
            {
                runId = run.RunId,
                chatId = run.ChatId,
                title,
                status = run.Status,
                error = run.Error,
                snippet = Snippet(run.PartialContent),
            },
            cancellationToken);
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
