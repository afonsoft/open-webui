namespace OpenWebUI.Application.Contracts;

/// <summary>Pedido de envio de mensagem que dispara uma run desacoplada.</summary>
/// <param name="Content">Texto da mensagem do usuário; null para regenerar a resposta sobre o histórico existente (última mensagem deve ser do usuário).</param>
/// <param name="Model">Modelo de geração pedido.</param>
/// <param name="FileIds">Ids de arquivos cujo conteúdo entra como contexto (opcional).</param>
/// <param name="ToolIds">Ids de tools habilitadas para a run (opcional).</param>
/// <param name="Params">Parâmetros de geração (temperature, top_p, max_tokens), opcional.</param>
/// <param name="WebSearch">Quando true, injeta resultados de busca web como contexto.</param>
public sealed record EnqueueChatRunRequest(
    string? Content,
    string Model,
    IReadOnlyList<string>? FileIds = null,
    IReadOnlyList<string>? ToolIds = null,
    IReadOnlyDictionary<string, object>? Params = null,
    bool? WebSearch = null);

/// <summary>Estado serializável de uma run desacoplada de chat.</summary>
/// <param name="Id">Identificador da run.</param>
/// <param name="ChatId">Chat ao qual a run pertence.</param>
/// <param name="Status">queued | running | completed | failed | stopped | interrupted.</param>
/// <param name="Model">Modelo pedido no envio.</param>
/// <param name="PartialContent">Conteúdo parcial do assistant (checkpoint por iteração).</param>
/// <param name="Error">Erro final quando failed.</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
/// <param name="StartedAt">Início da execução; null enquanto queued.</param>
/// <param name="CompletedAt">Finalização; null enquanto ativa.</param>
public sealed record ChatRunResponse(
    string Id,
    string ChatId,
    string Status,
    string Model,
    string? PartialContent,
    string? Error,
    long CreatedAt,
    long? StartedAt,
    long? CompletedAt);

/// <summary>
/// Evento SSE <c>tool_call</c> (SPEC-20261007-chat-tool-streaming RF-001):
/// emitido antes de executar a tool; <paramref name="ArgsPreview"/> é o JSON
/// de argumentos truncado e higienizado (sem secrets).
/// </summary>
public sealed record RunToolCallEvent(string Id, string Name, string? ArgsPreview);

/// <summary>
/// Evento SSE <c>tool_result</c>: <paramref name="Ok"/> false em erro ou
/// negação (<paramref name="Denied"/>); <paramref name="Preview"/> truncado;
/// <paramref name="ImagePath"/> quando a tool gerou imagem renderizável.
/// </summary>
public sealed record RunToolResultEvent(
    string Id, string Name, bool Ok, string? Preview,
    string? ImagePath = null, bool Denied = false);

/// <summary>
/// Evento SSE <c>status</c> de fase (RF-002): <paramref name="Phase"/> ∈
/// <c>generating</c> | <c>running_tool</c> | <c>awaiting_approval</c>;
/// <paramref name="Label"/> é o texto exibido no chip de status.
/// </summary>
public sealed record RunPhaseEvent(string Phase, string? Label);

/// <summary>
/// Evento SSE <c>approval_asked</c> (RF-003): a run pausou aguardando
/// decisão do dono sobre a tool mutável <paramref name="ToolName"/>.
/// <paramref name="Kind"/> descreve o tipo de execução (python|mcp|http).
/// </summary>
public sealed record RunApprovalAskedEvent(
    string CallId, string ToolName, string Kind, string? ArgsPreview);

/// <summary>
/// Item da lista de tarefas da run (SPEC-20261007-chat-agent-parity
/// RF-010): snapshot emitido pela builtin <c>todo_write</c>.
/// </summary>
/// <param name="Id">Id estável (t1, t2, ...).</param>
/// <param name="Content">Descrição da tarefa.</param>
/// <param name="Status">pending | in_progress | completed.</param>
public sealed record RunTaskItem(string Id, string Content, string Status);

/// <summary>
/// Evento SSE <c>tasks</c> (RF-010): snapshot da lista de tarefas da run
/// — o executor republica o array <c>tasks</c> do resultado estruturado
/// de <c>todo_write</c> para o painel/checklist do cliente.
/// </summary>
public sealed record RunTasksEvent(IReadOnlyList<RunTaskItem> Tasks);

/// <summary>
/// Arquivo alterado por file_write/file_edit numa run (SPEC-20261007-
/// chat-agent-parity RF-015): <paramref name="Diff"/> é o unificado da
/// última alteração naquele path.
/// </summary>
public sealed record RunChangeItem(
    string Path, int Added, int Removed, string? Diff);

/// <summary>
/// Evento SSE <c>changes</c> (RF-015): snapshot dos arquivos alterados
/// pela run — o executor acumula por path e republica a cada file_*,
/// para a aba Changes do painel de workspace.
/// </summary>
public sealed record RunChangesEvent(IReadOnlyList<RunChangeItem> Changes);

/// <summary>
/// Projeção de um <c>ChatJob</c> para o cliente (lista da aba Jobs do
/// painel, <c>GET /api/v1/jobs</c>).
/// </summary>
public sealed record ChatJobResponse(
    string Id,
    string? ChatId,
    string? RunId,
    string Command,
    string Status,
    int? Pid,
    int? ExitCode,
    string? Error,
    long StartedAt,
    long? FinishedAt);

/// <summary>Decisão do dono sobre uma aprovação pendente (RF-003).</summary>
/// <param name="Decision"><c>approve</c> | <c>deny</c>.</param>
/// <param name="Remember">Quando true, aprova a tool pelo resto da conversa.</param>
/// <param name="Message">
/// Instrução opcional numa negação (SPEC-20261007-chat-agent-ux RF-002):
/// vai como resultado da tool para o modelo corrigir a rota.
/// </param>
public sealed record RunApprovalDecisionRequest(
    string Decision, bool Remember = false, string? Message = null);

/// <summary>
/// Decisão do gate de tools (SPEC-20261007-chat-agent-ux): aprovado, ou
/// negado com mensagem opcional de instrução para o modelo.
/// </summary>
public sealed record ToolGateDecision(bool Approved, string? DenyMessage = null)
{
    /// <summary>Aprovado.</summary>
    public static readonly ToolGateDecision Allow = new(true);

    /// <summary>Negado sem instrução.</summary>
    public static readonly ToolGateDecision Deny = new(false);
}

/// <summary>Atualização parcial do chat (preset de aprovação de tools).</summary>
/// <param name="ApprovalPreset">
/// <c>allow-readonly</c> | <c>approve-mutations</c> | <c>always-allow</c>
/// (RF-004). Null não altera.
/// </param>
public sealed record ChatPatchRequest(string? ApprovalPreset);
