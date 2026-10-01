namespace OpenWebUI.Application.Contracts;

/// <summary>Automação exibida na lista do usuário.</summary>
public sealed record AutomationResponse(
    string Id,
    string Name,
    string Prompt,
    string ModelId,
    string ScheduleKind,
    int IntervalMinutes,
    string? TimeOfDay,
    int? Weekday,
    bool Enabled,
    long? NextRunAt,
    long CreatedAt,
    long UpdatedAt);

/// <summary>Criação/atualização de automação.</summary>
/// <param name="ScheduleKind">interval | daily | weekly.</param>
/// <param name="IntervalMinutes">Minutos entre execuções (kind=interval, mín. 1).</param>
/// <param name="TimeOfDay">"HH:mm" em UTC (kind=daily/weekly).</param>
/// <param name="Weekday">0-6 domingo..sábado em UTC (kind=weekly).</param>
public sealed record AutomationUpsertRequest(
    string Name,
    string Prompt,
    string ModelId,
    string ScheduleKind,
    int? IntervalMinutes,
    string? TimeOfDay,
    int? Weekday,
    bool? Enabled);

/// <summary>Execução registrada de uma automação.</summary>
public sealed record AutomationRunResponse(
    string Id,
    string AutomationId,
    string AutomationName,
    string Status,
    string? Error,
    string? ChatId,
    long StartedAt,
    long FinishedAt);
