namespace OpenWebUI.Application.Contracts;

/// <summary>Grant de acesso a um recurso compartilhável.</summary>
/// <param name="PrincipalType">"user" ou "group".</param>
/// <param name="PrincipalId">Id do usuário/grupo, ou "*" para todos os usuários.</param>
/// <param name="Permission">"read" ou "write".</param>
public sealed record AccessGrant(string PrincipalType, string PrincipalId, string Permission);

/// <summary>Atualização dos grants de um recurso (somente owner/admin).</summary>
/// <param name="AccessGrants">Lista completa de grants (substituição).</param>
public sealed record AccessUpdateRequest(List<AccessGrant> AccessGrants);

/// <summary>Calendário em respostas de API.</summary>
public sealed record CalendarResponse(
    string Id, string Name, string? Color, List<AccessGrant> AccessGrants, long CreatedAt);

/// <summary>Criação de calendário.</summary>
public sealed record CreateCalendarRequest(string Name, string? Color);

/// <summary>Atualização de calendário.</summary>
public sealed record UpdateCalendarRequest(string? Name, string? Color);

/// <summary>Evento de calendário em respostas de API.</summary>
public sealed record CalendarEventResponse(
    string Id, string CalendarId, string Title, long StartTs, long EndTs,
    string? Color, string? Notes, long CreatedAt);

/// <summary>Criação de evento.</summary>
public sealed record CreateEventRequest(
    string CalendarId, string Title, long StartTs, long EndTs, string? Color, string? Notes);

/// <summary>Atualização de evento.</summary>
public sealed record UpdateEventRequest(
    string? Title, long? StartTs, long? EndTs, string? Color, string? Notes);
