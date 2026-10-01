using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.JSInterop;
using OpenWebUI.Client.Services;

namespace OpenWebUI.Client.Tests;

/// <summary>Testes do serviço de localização do cliente (i18n).</summary>
[TestFixture]
public class LocalizationServiceTests
{
    private static readonly Dictionary<string, string> PtBr = new()
    {
        ["chat.send"] = "Enviar",
        ["channel.typing"] = "{{name}} está digitando…",
        ["settings.language"] = "Idioma",
    };

    private static readonly Dictionary<string, string> EnUs = new()
    {
        ["chat.send"] = "Send",
        ["channel.typing"] = "{{name}} is typing…",
        ["settings.language"] = "Language",
    };

    [Test]
    public async Task Initialize_CarregaPtBrComoPadrao()
    {
        var (service, _) = CreateService(storedLanguage: null);
        await service.InitializeAsync();

        Assert.Multiple(() =>
        {
            Assert.That(service.Language, Is.EqualTo("pt-BR"));
            Assert.That(service["chat.send"], Is.EqualTo("Enviar"));
        });
    }

    [Test]
    public void ChaveAusente_RetornaAPropriaChave()
    {
        var (service, _) = CreateService(storedLanguage: null);
        Assert.That(service["chave.inexistente"], Is.EqualTo("chave.inexistente"));
    }

    [Test]
    public async Task Interpolacao_SubstituiPlaceholders()
    {
        var (service, _) = CreateService(storedLanguage: null);
        await service.InitializeAsync();

        var result = service.T("channel.typing", ("name", "Ana"));
        Assert.That(result, Is.EqualTo("Ana está digitando…"));
    }

    [Test]
    public async Task SetLanguage_TrocaIdiomaEPersiste()
    {
        var (service, js) = CreateService(storedLanguage: null);
        await service.InitializeAsync();
        await service.SetLanguageAsync("en-US");

        Assert.Multiple(() =>
        {
            Assert.That(service.Language, Is.EqualTo("en-US"));
            Assert.That(service["chat.send"], Is.EqualTo("Send"));
            Assert.That(js.Stored[LocalizationService.StorageKey], Is.EqualTo("en-US"));
        });
    }

    [Test]
    public async Task SetLanguage_DisparaEventoChanged()
    {
        var (service, _) = CreateService(storedLanguage: null);
        await service.InitializeAsync();
        var fired = 0;
        service.Changed += () => fired++;

        await service.SetLanguageAsync("en-US");
        Assert.That(fired, Is.EqualTo(1));
    }

    [Test]
    public async Task IdiomaInvalidoPersistido_CaiNoPadrao()
    {
        var (service, _) = CreateService(storedLanguage: "fr-FR");
        await service.InitializeAsync();

        Assert.Multiple(() =>
        {
            Assert.That(service.Language, Is.EqualTo("pt-BR"));
            Assert.That(service["settings.language"], Is.EqualTo("Idioma"));
        });
    }

    [Test]
    public async Task FallbackDeChave_UsaPtBrQuandoFaltaNoIdiomaAtivo()
    {
        // en-US incompleto: channel.typing existe só em pt-BR.
        var (service, _) = CreateService(storedLanguage: null,
            en: new Dictionary<string, string> { ["chat.send"] = "Send" });
        await service.InitializeAsync();
        await service.SetLanguageAsync("en-US");

        Assert.That(service["channel.typing"], Is.EqualTo("{{name}} está digitando…"));
    }

    private static (LocalizationService Service, FakeJs Js) CreateService(
        string? storedLanguage, Dictionary<string, string>? en = null)
    {
        var dicts = new Dictionary<string, Dictionary<string, string>>
        {
            ["pt-BR"] = PtBr,
            ["en-US"] = en ?? EnUs,
        };

        var http = new HttpClient(new StubHandler((request, _) =>
        {
            var lang = request.RequestUri!.ToString().Split('/').Last().Replace(".json", "");
            var body = dicts.TryGetValue(lang, out var dict) ? dict : new Dictionary<string, string>();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            });
        }))
        {
            BaseAddress = new Uri("http://localhost/"),
        };

        var js = new FakeJs();
        if (storedLanguage is not null)
        {
            js.Stored[LocalizationService.StorageKey] = storedLanguage;
        }

        return (new LocalizationService(http, js), js);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    private sealed class FakeJs : IJSRuntime
    {
        public Dictionary<string, string?> Stored { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "localStorage.getItem" && args?[0] is string key)
            {
                var value = Stored.TryGetValue(key, out var stored) ? stored : null;
                return new ValueTask<TValue>((TValue)(object?)value!);
            }

            if (identifier == "localStorage.setItem" && args is [string k, string newValue])
            {
                Stored[k] = newValue;
                return new ValueTask<TValue>();
            }

            return new ValueTask<TValue>();
        }
    }
}
