using Microsoft.JSInterop;

namespace OpenWebUI.Client.Services;

/// <summary>Acesso a localStorage do navegador via JS interop.</summary>
public class BrowserStorage(IJSRuntime js)
{
    /// <summary>Lê um item do localStorage.</summary>
    public ValueTask<string?> GetAsync(string key) =>
        js.InvokeAsync<string?>("localStorage.getItem", key);

    /// <summary>Grava um item no localStorage.</summary>
    public ValueTask SetAsync(string key, string value) =>
        js.InvokeVoidAsync("localStorage.setItem", key, value);

    /// <summary>Remove um item do localStorage.</summary>
    public ValueTask RemoveAsync(string key) =>
        js.InvokeVoidAsync("localStorage.removeItem", key);
}
