namespace OpenWebUI.Application.Interfaces;

/// <summary>
/// Seam de notificação de run (SPEC-20261007-chat-notifications RF-001):
/// o dispatcher chama <see cref="RunCompletedAsync"/> uma vez por run quando
/// ela chega a um status terminal (completed/failed/stopped/interrupted).
/// Implementações registradas recebem o evento em sequência; a notificação é
/// best-effort e nunca deve falhar a run.
/// </summary>
public interface IChatRunNotifier
{
    /// <summary>Chamado uma vez por run ao atingir status terminal.</summary>
    /// <param name="run">Resumo da run finalizada.</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    Task RunCompletedAsync(ChatRunFinished run, CancellationToken cancellationToken);
}

/// <summary>
/// Snapshot terminal de uma run de chat enviado aos notifiers —
/// contrato puro (a camada Application não referencia as entidades do Domain).
/// </summary>
/// <param name="RunId">Identificador da run.</param>
/// <param name="ChatId">Chat ao qual a run pertence.</param>
/// <param name="UserId">Dono da run (isolamento).</param>
/// <param name="Model">Modelo pedido.</param>
/// <param name="Status">Status terminal (completed/failed/stopped/interrupted).</param>
/// <param name="Error">Erro final, quando failed.</param>
/// <param name="PartialContent">Conteúdo gerado (para trecho na notificação).</param>
public sealed record ChatRunFinished(
    string RunId, string ChatId, string UserId, string Model,
    string Status, string? Error, string? PartialContent);
