using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes de hosting PWA (slice pwa-offline): documento revalida e assets do
/// manifest/service worker são servidos.
/// </summary>
[TestFixture]
public class PwaTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-pwa-{Guid.NewGuid():N}.db");
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
            File.Delete(_dbPath);
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
}
