using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Client.Services;

/// <summary>
/// Estado vivo de uma tool_call + seu tool_result (SPEC chat-activity-feed):
/// compartilhado entre ChatView (eventos SSE e mensagens persistidas) e
/// RunActivityFeed (linhas amigáveis + detalhes via ToolCallCard).
/// </summary>
public sealed class ToolCallView
{
    /// <summary>Evento tool_call (id, nome, preview de args).</summary>
    public required RunToolCallEvent Call { get; init; }

    /// <summary>tool_result correspondente quando chega; null = ainda rodando.</summary>
    public RunToolResultEvent? Result { get; set; }
}
