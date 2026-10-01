namespace OpenWebUI.Application.Contracts;

/// <summary>Resposta agregada do dashboard de analytics (apenas contagens).</summary>
/// <param name="Users">Totais de usuários.</param>
/// <param name="Series">Séries temporais por dia (UTC, yyyy-MM-dd).</param>
/// <param name="Models">Top modelos por mensagens assistant no período.</param>
/// <param name="Evaluations">Totais de avaliações no período.</param>
public sealed record AnalyticsResponse(
    AnalyticsUsers Users,
    AnalyticsSeries Series,
    IReadOnlyList<ModelUsageCount> Models,
    AnalyticsEvaluations Evaluations);

/// <summary>Totais de usuários.</summary>
/// <param name="Total">Usuários cadastrados.</param>
/// <param name="New">Novos usuários no período.</param>
public sealed record AnalyticsUsers(int Total, int New);

/// <summary>Séries temporais do período.</summary>
/// <param name="Messages">Mensagens por dia.</param>
/// <param name="Chats">Chats criados por dia.</param>
public sealed record AnalyticsSeries(
    IReadOnlyList<DayCount> Messages,
    IReadOnlyList<DayCount> Chats);

/// <summary>Contagem em um dia (yyyy-MM-dd UTC).</summary>
public sealed record DayCount(string Day, int Count);

/// <summary>Uso de um modelo no período.</summary>
public sealed record ModelUsageCount(string Model, int Count);

/// <summary>Avaliações de mensagens no período.</summary>
public sealed record AnalyticsEvaluations(int Positive, int Negative);
