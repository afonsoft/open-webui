namespace OpenWebUI.Application.Contracts;

/// <summary>Pedido de configuração de webhook de notificação.</summary>
/// <param name="Url">URL de destino (http/https).</param>
/// <param name="Events">Eventos habilitados (ex.: user.pending, user.approved, automation.failed, channel.mention).</param>
/// <param name="Enabled">Se o webhook está ativo.</param>
public sealed record NotificationWebhookRequest(string Url, List<string> Events, bool Enabled = true);

/// <summary>Webhook de notificação nas leituras (segredo nunca exposto).</summary>
/// <param name="Id">Identificador do webhook.</param>
/// <param name="Url">URL configurada.</param>
/// <param name="Events">Eventos habilitados.</param>
/// <param name="Enabled">Se está ativo.</param>
public sealed record NotificationWebhookResponse(string Id, string Url, List<string> Events, bool Enabled);

/// <summary>Resultado do disparo de teste para o webhook.</summary>
/// <param name="StatusCode">Código HTTP retornado pelo destino (0 quando inalcançável).</param>
/// <param name="Ok">Se o destino respondeu sucesso (2xx).</param>
public sealed record WebhookTestResponse(int StatusCode, bool Ok);
