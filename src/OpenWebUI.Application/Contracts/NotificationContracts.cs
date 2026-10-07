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

/// <summary>Chaves de criptografia de uma subscription Web Push (sub.toJSON().keys).</summary>
/// <param name="P256dh">Chave pública ECDH do cliente (base64url).</param>
/// <param name="Auth">Segredo de auth do cliente (base64url).</param>
public sealed record PushSubscriptionKeys(string P256dh, string Auth);

/// <summary>
/// Registro/atualização de subscription Web Push
/// (SPEC-20261007-chat-notifications) — o corpo segue o formato de
/// <c>PushSubscription.toJSON()</c> mais <c>userAgent</c> para debug.
/// </summary>
/// <param name="Endpoint">Endpoint do push service.</param>
/// <param name="Keys">Material de criptografia do cliente.</param>
/// <param name="UserAgent">User-Agent do navegador (opcional).</param>
public sealed record PushSubscriptionRequest(
    string Endpoint, PushSubscriptionKeys Keys, string? UserAgent = null);

/// <summary>Chave pública VAPID para pushManager.subscribe.</summary>
/// <param name="PublicKey">Chave no formato base64url (ponto descomprimido P-256).</param>
public sealed record VapidPublicKeyResponse(string PublicKey);
