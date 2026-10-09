namespace OpenWebUI.Application.Contracts;

/// <summary>
/// Contratos do runner de testes do workspace
/// (SPEC-20261009-ide-mentions-tests, RF-003/RF-004).
/// </summary>

/// <summary>Pedido para iniciar um test run; <paramref name="Confirmed"/> confirma comandos que exigem aprovação (WorkspaceWrite).</summary>
public sealed record TestRunStartRequest(bool Confirmed = false);

/// <summary>
/// Resposta do POST /test-run: <paramref name="RequiresApproval"/> sinaliza o cartão
/// de aprovação (comando visível antes de rodar); <paramref name="JobId"/> identifica o job criado.
/// </summary>
public sealed record TestRunStartResponse(
    string? JobId,
    string? Command,
    bool RequiresApproval,
    string? Reason);

/// <summary>Contagens parseadas do runner; null quando a saída não traz contagens (nunca inventadas).</summary>
public sealed record TestRunSummary(
    int? Passed,
    int? Failed,
    int? Skipped,
    long? DurationMs);

/// <summary>Resposta do GET /test-run/{jobId}: estado do job + resumo + cauda do log.</summary>
public sealed record TestRunStatusResponse(
    string State,
    TestRunSummary? Summary,
    string Tail,
    string? Command,
    int? ExitCode,
    string? Error);

/// <summary>Pedido para definir/limpar o TestCommand customizado do binding.</summary>
public sealed record TestCommandRequest(string? TestCommand);
