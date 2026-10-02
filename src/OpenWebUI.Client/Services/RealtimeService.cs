using Microsoft.AspNetCore.SignalR.Client;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Client.Services;

/// <summary>
/// Conexão realtime (SignalR /ws) para canais: recebe message:new,
/// user:typing e presence; envia indicador de digitação.
/// Reconecta automaticamente; falha do hub não quebra o REST.
/// </summary>
public class RealtimeService : IAsyncDisposable
{
    private HubConnection? _connection;
    private string? _token;

    /// <summary>Nova mensagem de canal recebida.</summary>
    public event Action<ChannelMessageResponse>? OnMessage;

    /// <summary>Indicador de digitação (channelId, userId, name).</summary>
    public event Action<string, string, string>? OnTyping;

    /// <summary>Presença atualizada (channelId, userIds online).</summary>
    public event Action<string, string[]>? OnPresence;

    /// <summary>Reações de uma mensagem atualizadas (channelId, messageId, agregado).</summary>
    public event Action<string, string, List<ChannelReactionResponse>>? OnReaction;

    /// <summary>Mensagem atualizada (channelId, messageId, isPinned).</summary>
    public event Action<string, string, bool>? OnMessageUpdate;

    /// <summary>Se a conexão com o hub está ativa.</summary>
    public bool Connected => _connection?.State == HubConnectionState.Connected;

    /// <summary>Conecta ao hub com o JWT do usuário (idempotente).</summary>
    /// <param name="baseUri">Endereço base do servidor.</param>
    /// <param name="token">JWT atual.</param>
    public async Task ConnectAsync(string baseUri, string? token)
    {
        if (token is null)
        {
            return;
        }
        if (_connection is not null)
        {
            if (_token == token)
            {
                return;
            }
            await _connection.DisposeAsync();
            _connection = null;
        }

        _token = token;
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(baseUri), "ws"), options =>
                options.AccessTokenProvider = () => Task.FromResult<string?>(_token))
            .WithAutomaticReconnect()
            .Build();

        _connection.On<ChannelMessageResponse>(
            "message:new", m => OnMessage?.Invoke(m));
        _connection.On<string, string, string>(
            "user:typing", (c, u, n) => OnTyping?.Invoke(c, u, n));
        _connection.On<string, string[]>(
            "presence", (c, users) => OnPresence?.Invoke(c, users));
        _connection.On<ReactionEvent>(
            "reaction", e => OnReaction?.Invoke(e.ChannelId, e.MessageId, e.Reactions));
        _connection.On<MessageUpdateEvent>(
            "message:update", e => OnMessageUpdate?.Invoke(e.ChannelId, e.Id, e.IsPinned));

        try
        {
            await _connection.StartAsync();
        }
        catch (Exception)
        {
            // Hub indisponível: REST segue funcionando sem realtime.
        }
    }

    /// <summary>Envia indicador de digitação ao canal.</summary>
    /// <param name="channelId">Canal alvo.</param>
    public async Task SendTypingAsync(string channelId)
    {
        if (Connected)
        {
            try
            {
                await _connection!.InvokeAsync("Typing", channelId);
            }
            catch (Exception)
            {
            }
        }
    }

    private sealed record ReactionEvent(
        [property: System.Text.Json.Serialization.JsonPropertyName("channel_id")] string ChannelId,
        [property: System.Text.Json.Serialization.JsonPropertyName("message_id")] string MessageId,
        List<ChannelReactionResponse> Reactions);

    private sealed record MessageUpdateEvent(
        [property: System.Text.Json.Serialization.JsonPropertyName("channel_id")] string ChannelId,
        string Id,
        [property: System.Text.Json.Serialization.JsonPropertyName("is_pinned")] bool IsPinned);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }
}
