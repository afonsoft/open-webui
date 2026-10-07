namespace OpenWebUI.Application.Interfaces;

/// <summary>
/// Envia uma notificação Web Push (SPEC-20261007-chat-notifications RF-004).
/// O contrato isola a biblioteca de criptografia/transporte do notifier
/// (e dos testes).
/// </summary>
public interface IWebPushSender
{
    /// <summary>
    /// Resultado do envio: <see cref="Sent"/>, ou falha com o HTTP status
    /// opcional — 404/410 = subscription morta para sempre (RFC 8030 §7.3).
    /// </summary>
    /// <param name="Sent">Se o push service aceitou a entrega.</param>
    /// <param name="HttpStatus">Status devolvido pelo push service, quando conhecido.</param>
    /// <param name="Error">Mensagem de erro.</param>
    public sealed record Result(bool Sent, int? HttpStatus = null, string? Error = null);

    /// <summary>Entrega <paramref name="payloadJson"/> ao endpoint da subscription.</summary>
    /// <param name="endpoint">Endpoint do push service.</param>
    /// <param name="p256dh">Chave pública ECDH do cliente (base64url).</param>
    /// <param name="auth">Segredo de auth do cliente (base64url).</param>
    /// <param name="payloadJson">Payload JSON da notificação.</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    Task<Result> SendAsync(
        string endpoint, string p256dh, string auth,
        string payloadJson, CancellationToken cancellationToken = default);
}
