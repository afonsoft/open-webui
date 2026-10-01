using Microsoft.JSInterop;

namespace OpenWebUI.Client.Services;

/// <summary>Gerencia o tema claro/escuro aplicado ao documento.</summary>
public class ThemeService(IJSRuntime js, BrowserStorage storage)
{
    private const string ThemeKey = "webui.theme";

    /// <summary>Tema atual: "dark" ou "light".</summary>
    public string Current { get; private set; } = "dark";

    /// <summary>Disparado quando o tema muda.</summary>
    public event Action? Changed;

    /// <summary>Carrega o tema persistido e o aplica ao documento.</summary>
    public async Task InitializeAsync()
    {
        var saved = await storage.GetAsync(ThemeKey);
        Current = saved is "light" or "dark" ? saved : "dark";
        await ApplyAsync();
    }

    /// <summary>Alterna entre claro e escuro.</summary>
    public async Task SetAsync(string theme)
    {
        Current = theme;
        await storage.SetAsync(ThemeKey, theme);
        await ApplyAsync();
        Changed?.Invoke();
    }

    private Task ApplyAsync() =>
        js.InvokeVoidAsync("openwebui.setTheme", Current).AsTask();
}

/// <summary>Barramento leve para sincronizar a lista de chats da barra lateral.</summary>
public class ChatListState
{
    /// <summary>Disparado quando a lista de chats deve ser recarregada.</summary>
    public event Action? Changed;

    /// <summary>Solicita a atualização da lista.</summary>
    public void NotifyChanged() => Changed?.Invoke();
}
