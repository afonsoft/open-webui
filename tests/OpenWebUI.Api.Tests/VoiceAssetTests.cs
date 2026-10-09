using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Teste de hosting do slice voice: o helper JS de áudio (STT/TTS via Web
/// Speech API) precisa ser servido com o restante do app Blazor.
/// </summary>
[TestFixture, IsolateEnvironment]
public class VoiceAssetTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-voice-{Guid.NewGuid():N}.db");
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
    public async Task AudioJs_EhServido()
    {
        var response = await _client.GetAsync("/js/audio.js");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("openwebui.audio"));
            Assert.That(body, Does.Contain("startStt"));
            Assert.That(body, Does.Contain("speechSynthesis"));
        });
    }

    [Test]
    public async Task Index_CarregaAudioJs()
    {
        var response = await _client.GetAsync("/");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("js/audio.js"));
    }
}
