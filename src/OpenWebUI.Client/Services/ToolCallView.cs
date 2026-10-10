using System.Text.Json;
using System.Text.RegularExpressions;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Client.Services;

/// <summary>
/// Estado vivo de uma tool_call + seu tool_result (SPEC chat-activity-feed):
/// compartilhado entre ChatView (eventos SSE e mensagens persistidas) e
/// RunActivityFeed (linhas amigáveis + detalhes via ToolCallCard).
/// </summary>
public sealed partial class ToolCallView
{
    /// <summary>Evento tool_call (id, nome, preview de args).</summary>
    public required RunToolCallEvent Call { get; init; }

    /// <summary>tool_result correspondente quando chega; null = ainda rodando.</summary>
    public RunToolResultEvent? Result { get; set; }

    /// <summary>
    /// Converte uma mensagem persistida role=tool em ToolCallView para o
    /// work log colapsado do histórico. Para delegate_task, reidrata o
    /// <c>childChatId</c> do link "(chat filho: /c/{id})" gravado no texto
    /// — é o que permite o card embutir a conversa da subtarefa (padrão
    /// Devin web) também no histórico, onde o Result estruturado do SSE
    /// não foi persistido.
    /// </summary>
    public static ToolCallView FromPersisted(ChatMessageModel m)
    {
        var name = m.ToolCallId ?? "tool";
        return new ToolCallView
        {
            Call = new RunToolCallEvent(name, name, null),
            Result = new RunToolResultEvent(
                name, name, true, m.Content,
                Result: DelegateResult(m)),
        };
    }

    private static JsonElement? DelegateResult(ChatMessageModel m)
    {
        var name = m.ToolCallId ?? string.Empty;
        if (!name.EndsWith("delegate_task", StringComparison.Ordinal)
            || m.Content is null)
        {
            return null;
        }
        var match = ChildChatLink().Match(m.Content);
        return match.Success
            ? JsonSerializer.SerializeToElement(new { childChatId = match.Groups[1].Value })
            : null;
    }

    [GeneratedRegex(@"/c/([A-Za-z0-9_-]+)")]
    private static partial Regex ChildChatLink();
}
