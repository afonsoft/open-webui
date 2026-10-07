namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Contexto de execução das tools built-in
/// (SPEC-20261007-chat-agent-tools): quem chamou, em qual chat/run e o
/// diretório de trabalho confinado (<c>data/workspaces/{userId}</c>).
/// Construído no endpoint/executor — nunca por a tool.
/// </summary>
/// <param name="UserId">Dono da conversa (isolamento).</param>
/// <param name="ChatId">Chat da run, quando conhecido.</param>
/// <param name="RunId">Run ativa, quando dentro do dispatcher.</param>
/// <param name="WorkspacePath">Diretório de trabalho do usuário.</param>
/// <param name="UploadDir">Diretório de uploads do usuário (imagens geradas).</param>
public sealed record BuiltinToolContext(
    string UserId,
    string? ChatId,
    string? RunId,
    string WorkspacePath,
    string UploadDir);
