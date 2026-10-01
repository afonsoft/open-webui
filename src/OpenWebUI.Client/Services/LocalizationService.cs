using System.Net.Http.Json;
using Microsoft.JSInterop;

namespace OpenWebUI.Client.Services;

/// <summary>
/// Serviço de localização do cliente: carrega dicionários JSON de
/// <c>wwwroot/i18n/{lang}.json</c>, resolve chaves com fallback para pt-BR
/// e notifica componentes quando o idioma muda (sem reload).
/// </summary>
public sealed class LocalizationService(HttpClient http, IJSRuntime js)
{
    /// <summary>Idioma padrão e fallback de chaves ausentes.</summary>
    public const string DefaultLanguage = "pt-BR";

    /// <summary>Chave do localStorage onde o idioma escolhido persiste.</summary>
    public const string StorageKey = "webui.locale";

    /// <summary>Idiomas suportados (código → nome exibido no seletor).</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> Languages =
    [
        ("pt-BR", "Português (Brasil)"),
        ("en-US", "English (US)"),
    ];

    private Dictionary<string, string> _active = new();
    private Dictionary<string, string> _fallback = new();

    /// <summary>Idioma atual (código, ex.: "pt-BR").</summary>
    public string Language { get; private set; } = DefaultLanguage;

    /// <summary>Disparado quando o idioma muda — componentes devem re-renderizar.</summary>
    public event Action? Changed;

    /// <summary>Resolve uma chave para a string localizada.</summary>
    public string this[string key] => T(key);

    /// <summary>Resolve uma chave; fallback para pt-BR e, por último, a própria chave.</summary>
    public string T(string key) =>
        _active.TryGetValue(key, out var value) || _fallback.TryGetValue(key, out value)
            ? value
            : key;

    /// <summary>Resolve uma chave e interpola placeholders <c>{{nome}}</c>.</summary>
    public string T(string key, params (string Name, string Value)[] args)
    {
        var text = T(key);
        foreach (var (name, value) in args)
        {
            text = text.Replace("{{" + name + "}}", value, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>Carrega o idioma persistido (ou padrão) — chamado antes do primeiro render.</summary>
    public async Task InitializeAsync()
    {
        _fallback = await LoadAsync(DefaultLanguage);
        string? stored = null;
        try
        {
            stored = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
        }
        catch (JSException)
        {
            // localStorage indisponível — segue com o padrão.
        }

        await ApplyAsync(stored);
    }

    /// <summary>Troca o idioma, persiste em localStorage e notifica a UI.</summary>
    public async Task SetLanguageAsync(string language)
    {
        await ApplyAsync(language);
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", StorageKey, Language);
        }
        catch (JSException)
        {
            // Sem persistência — troca ainda vale para a sessão.
        }

        Changed?.Invoke();
    }

    private async Task ApplyAsync(string? language)
    {
        var lang = Languages.Any(l => l.Code == language) ? language! : DefaultLanguage;
        _active = lang == DefaultLanguage ? _fallback : await LoadAsync(lang);
        Language = lang;
    }

    private async Task<Dictionary<string, string>> LoadAsync(string language)
    {
        try
        {
            return await http.GetFromJsonAsync<Dictionary<string, string>>($"i18n/{language}.json")
                ?? new Dictionary<string, string>();
        }
        catch (HttpRequestException)
        {
            return new Dictionary<string, string>();
        }
    }
}
