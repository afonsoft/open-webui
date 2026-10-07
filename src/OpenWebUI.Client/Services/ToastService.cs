namespace OpenWebUI.Client.Services;

/// <summary>
/// Toasts in-app (SPEC-20261007-chat-notifications): serviço singleton —
/// <see cref="ToastHost"/> renderiza a fila no canto da tela.
/// </summary>
public sealed class ToastService
{
    /// <summary>Novo toast para exibir.</summary>
    public event Action<ToastMessage>? OnShow;

    /// <summary>Exibe um toast. <paramref name="url"/> opcional navega ao clicar.</summary>
    /// <param name="text">Texto exibido.</param>
    /// <param name="type">Severidade visual.</param>
    /// <param name="url">Destino ao clicar (opcional).</param>
    public void Show(string text, ToastType type = ToastType.Info, string? url = null) =>
        OnShow?.Invoke(new ToastMessage(text, type, url));
}

/// <summary>Severidade visual de um toast.</summary>
public enum ToastType
{
    /// <summary>Informação neutra.</summary>
    Info,

    /// <summary>Sucesso.</summary>
    Success,

    /// <summary>Alerta.</summary>
    Warning,

    /// <summary>Erro.</summary>
    Error,
}

/// <summary>Uma notificação toast.</summary>
/// <param name="Text">Texto exibido.</param>
/// <param name="Type">Severidade.</param>
/// <param name="Url">Destino ao clicar.</param>
public sealed record ToastMessage(string Text, ToastType Type, string? Url);
