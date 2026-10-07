namespace OpenWebUI.Application.Interfaces;

/// <summary>
/// Fila de despacho de runs de chat (SPEC-20261007-chat-agent-parity
/// RF-016): exposta na camada Application para que tools built-in em
/// Infrastructure (ex.: <c>delegate_task</c>) enfileirem runs filhas sem
/// conhecer a camada Api.
/// </summary>
public interface IChatRunDispatcher
{
    /// <summary>Enfileira uma run recém-criada (idempotente).</summary>
    /// <param name="runId">Id da run queued persistida.</param>
    void Enqueue(string runId);
}
