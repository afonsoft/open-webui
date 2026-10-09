using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do espelho /framework-assets/{stem}/{ext} — rota sem extensão para os
/// binários de _framework, para proxies corporativos que bloqueiam downloads
/// por extensão (.dat/.wasm/...) ou inspecionam payloads binários.
/// </summary>
[TestFixture, IsolateEnvironment]
public class FrameworkAssetsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-fwassets-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient(); // anônimo — deve servir antes do login
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

    private IFileInfo PickAsset(string extension)
    {
        var env = _factory.Services.GetRequiredService<IWebHostEnvironment>();
        var file = env.WebRootFileProvider.GetDirectoryContents("_framework")
            .FirstOrDefault(f => !f.IsDirectory && f.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        Assert.That(file, Is.Not.Null, "test host deve expor os static web assets de _framework");
        return file!;
    }

    private static bool HasSibling(IWebHostEnvironment env, string fileName, string suffix) =>
        env.WebRootFileProvider.GetFileInfo($"_framework/{fileName}{suffix}").Exists;

    private static (string Stem, string Ext) Split(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        Assert.That(dot, Is.GreaterThan(0), "asset fingerprinted deve ter extensão: " + fileName);
        return (fileName[..dot], fileName[(dot + 1)..]);
    }

    [Test]
    public async Task Get_KnownDatAsset_Retorna200OctetStreamImmutable()
    {
        var (stem, ext) = Split(PickAsset(".dat").Name);

        var response = await _client.GetAsync($"/framework-assets/{stem}/{ext}");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/octet-stream"));
            Assert.That(response.Headers.CacheControl?.ToString(), Does.Contain("immutable"));
        });
    }

    [Test]
    public async Task Get_KnownWasmAsset_RetornaApplicationWasm()
    {
        // MIME correto restaura WebAssembly.instantiateStreaming.
        var (stem, ext) = Split(PickAsset(".wasm").Name);

        var response = await _client.GetAsync($"/framework-assets/{stem}/{ext}");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/wasm"));
        });
    }

    [Test]
    public async Task Get_EncB64_RetornaTextPlainComRoundTripExato()
    {
        // O body base64 deve decodificar byte a byte para o arquivo real, para o
        // check SHA-256 do cliente bater com o hash de integridade do boot.
        var file = PickAsset(".dat");
        var (stem, ext) = Split(file.Name);

        var response = await _client.GetAsync($"/framework-assets/{stem}/{ext}?enc=b64");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/plain"));
            Assert.That(response.Headers.CacheControl?.ToString(), Does.Contain("immutable"));
        });
        await using var expected = new MemoryStream();
        await file.CreateReadStream().CopyToAsync(expected);
        Assert.That(Convert.FromBase64String(await response.Content.ReadAsStringAsync()),
            Is.EqualTo(expected.ToArray()));
    }

    [Test]
    public async Task Get_EncInvalido_CaiNosBytesCru()
    {
        var (stem, ext) = Split(PickAsset(".dat").Name);

        var response = await _client.GetAsync($"/framework-assets/{stem}/{ext}?enc=zip");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/octet-stream"));
        });
    }

    [Test]
    public async Task Get_AcceptEncoding_ServeIrmaoComprimido_QuandoExiste()
    {
        var env = _factory.Services.GetRequiredService<IWebHostEnvironment>();
        var name = PickAsset(".dat").Name;
        var hasBr = HasSibling(env, name, ".br");
        var hasGz = HasSibling(env, name, ".gz");
        Assert.That(hasBr || hasGz, Is.True, "esperado irmão comprimido para " + name);

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/framework-assets/{name[..name.LastIndexOf('.')]}/{name[(name.LastIndexOf('.') + 1)..]}");
        request.Headers.AcceptEncoding.ParseAdd("gzip, br");

        var response = await _client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var encoding = response.Content.Headers.ContentEncoding.ToString();
        Assert.That(encoding, Is.EqualTo(hasBr ? "br" : "gzip"));
    }

    [Test]
    public async Task Get_StemDesconhecido_Retorna404()
    {
        var response = await _client.GetAsync("/framework-assets/no-such-file.abc123/dat");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [TestCase("..foo", "dat")]      // fragmento de traversal no stem
    [TestCase("ok.stem", "D4T")]    // ext maiúscula rejeitada (regex é [a-z0-9])
    [TestCase("ok.stem", "d-at")]   // caractere ilegal na ext
    [TestCase("bad%20stem", "dat")] // caractere ilegal no stem
    public async Task Get_TraversalOuCaracteresIlegais_Retorna404(string stem, string ext)
    {
        // Nada fora de _framework pode ser servido pela rota separada.
        var response = await _client.GetAsync($"/framework-assets/{stem}/{ext}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
