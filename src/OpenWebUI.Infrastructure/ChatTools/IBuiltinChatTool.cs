using System.Text.Json;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Resultado de uma tool built-in: texto para o modelo no papel
/// <c>role=tool</c> e um payload JSON estruturado emitido como
/// <c>tool_result.result</c> no SSE — o cliente usa para renderização rica
/// (ex.: <c>{"imagePath": ...}</c> vira <c>&lt;img&gt;</c> inline no transcript;
/// <c>{"jobId": ...}</c> vira chip de job).
/// </summary>
/// <param name="Text">Texto retornado ao modelo.</param>
/// <param name="Result">Payload estruturado (null = só texto).</param>
/// <param name="Refused">Se a tool se recusou a executar por política.</param>
/// <param name="RefuseReason">Motivo da recusa.</param>
public sealed record BuiltinToolResult(
    string Text,
    object? Result = null,
    bool Refused = false,
    string? RefuseReason = null);

/// <summary>
/// Tool built-in do chat (SPEC-20261007-chat-agent-tools): implementação em
/// processo registrada com id <c>builtin:{name}</c>, despatched pelo
/// <see cref="Services.ToolExecutor"/> quando a URL do Tool é
/// <c>builtin://{name}</c>. Cada tool declara <see cref="ParametersJson"/>
/// (schema JSON que vai ao provider) e <see cref="RequiresApproval"/> (mutável
/// → gate do preset de tool safety).
/// </summary>
public interface IBuiltinChatTool
{
    /// <summary>Nome da função exposto ao modelo (anunciado como <c>builtin_{Name}</c>).</summary>
    string Name { get; }

    /// <summary>Descrição exposta ao modelo.</summary>
    string Description { get; }

    /// <summary>JSON Schema dos parâmetros (campo <c>parameters</c> do spec).</summary>
    string ParametersJson { get; }

    /// <summary>Se a execução tem efeitos colaterais e exige aprovação.</summary>
    bool RequiresApproval { get; }

    /// <summary>
    /// Executa a tool com os argumentos do modelo no contexto da run.
    /// </summary>
    /// <param name="args">Documento JSON dos argumentos do tool call.</param>
    /// <param name="context">Dono/chat/run/workspace.</param>
    /// <param name="ct">Token de cancelamento da run.</param>
    /// <returns>Texto para o modelo + payload estruturado para o cliente.</returns>
    Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct);
}
