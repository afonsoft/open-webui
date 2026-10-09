using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes de hosting PWA (slice pwa-offline): documento revalida e assets do
/// manifest/service worker são servidos.
/// </summary>
[TestFixture, IsolateEnvironment]
public class PwaTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-pwa-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    [Test]
    public async Task Index_ServeNoCache()
    {
        var response = await _client.GetAsync("/");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.CacheControl?.ToString(), Does.Contain("no-cache"));
    }

    [Test]
    public async Task RotaDaSpa_ServeDocumentoComNoCache()
    {
        var response = await _client.GetAsync("/admin");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.CacheControl?.ToString(), Does.Contain("no-cache"));
    }

    [Test]
    public async Task Manifest_ServiceWorker_EIcones_SaoServidos()
    {
        var manifest = await _client.GetAsync("/manifest.webmanifest");
        Assert.That(manifest.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await manifest.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("\"standalone\"").And.Contain("icon-192.png"));

        var sw = await _client.GetAsync("/service-worker.js");
        Assert.That(sw.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(sw.Headers.CacheControl?.ToString(), Does.Contain("no-cache"));

        var icon = await _client.GetAsync("/assets/icons/icon-512.png");
        Assert.That(icon.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task ServiceWorker_SeparaCacheImutavelDoShellMutavel()
    {
        // Regressão do bug pós-E16: um único cache-first servia bundle WASM
        // velho entre deploys. O SW precisa isolar o cache de framework
        // (fingerprinted, cache-first) do shell mutável (network-first).
        var sw = await _client.GetStringAsync("/service-worker.js");

        Assert.Multiple(() =>
        {
            Assert.That(sw, Does.Contain("openwebui-shell-v"));
            Assert.That(sw, Does.Contain("openwebui-fw-v"));
            Assert.That(sw, Does.Contain("networkFirst"));
            Assert.That(sw, Does.Contain("/framework-assets/"));
            Assert.That(sw, Does.Contain("/_framework/blazor.webassembly.js"));
        });
    }
}
