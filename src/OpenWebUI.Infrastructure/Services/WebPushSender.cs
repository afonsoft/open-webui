using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Interfaces;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// <see cref="IWebPushSender"/> sobre Lib.Net.Http.WebPush — envio assinado
/// VAPID (<c>RequestPushMessageDeliveryAsync</c>). A identidade vem de
/// <see cref="VapidKeyService"/> (auto-gerada e persistida no primeiro uso).
/// </summary>
public sealed class WebPushSender(
    VapidKeyService vapidKeys,
    IHttpClientFactory httpClientFactory,
    ILogger<WebPushSender> logger) : IWebPushSender
{
    /// <inheritdoc />
    public async Task<IWebPushSender.Result> SendAsync(
        string endpoint, string p256dh, string auth,
        string payloadJson, CancellationToken cancellationToken = default)
    {
        try
        {
            var keys = await vapidKeys.GetOrCreateAsync(cancellationToken);
            if (keys is null)
            {
                return new IWebPushSender.Result(Sent: false, Error: "VAPID não configurado.");
            }

            using var vapid = new VapidAuthentication(keys.PublicKey, keys.PrivateKey)
            {
                Subject = keys.Subject,
            };
            var subscription = new PushSubscription { Endpoint = endpoint };
            subscription.SetKey(PushEncryptionKeyName.P256DH, p256dh);
            subscription.SetKey(PushEncryptionKeyName.Auth, auth);
            var client = new PushServiceClient(httpClientFactory.CreateClient("webpush"));
            await client.RequestPushMessageDeliveryAsync(
                subscription, new PushMessage(payloadJson), vapid, cancellationToken);
            return new IWebPushSender.Result(Sent: true);
        }
        catch (PushServiceClientException ex)
        {
            return new IWebPushSender.Result(
                Sent: false, HttpStatus: (int)ex.StatusCode, Error: ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Falha no envio web push para {Endpoint}", Truncate(endpoint));
            return new IWebPushSender.Result(Sent: false, Error: ex.Message);
        }
    }

    private static string Truncate(string endpoint) =>
        endpoint.Length <= 60 ? endpoint : endpoint[..57] + "...";
}
