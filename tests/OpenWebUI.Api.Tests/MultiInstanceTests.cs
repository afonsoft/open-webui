using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Storage;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes da slice multi-instance: seleção de provider EF (sqlite/postgresql),
/// IFileStorage local/S3 e healthcheck enriquecido. Sem Postgres/Redis/S3 real —
/// cobertura de seleção e da lógica de storage com fake S3 em memória.
/// </summary>
[TestFixture]
[NonParallelizable]
public class MultiInstanceTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _tempDir = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-mi-{Guid.NewGuid():N}.db");
        _tempDir = Path.Combine(Path.GetTempPath(), $"openwebui-mi-{Guid.NewGuid():N}");
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
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static IConfiguration Config(params (string Key, string? Value)[] pairs)
    {
        var dict = pairs.ToDictionary(p => p.Key, p => p.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(dict!).Build();
    }

    // ---------------- Seleção de provider ----------------

    /// <summary>sqlite é o default; postgresql só com valor exato (case-insensitive).</summary>
    [Test]
    public void T01_ProviderSelector_Resolve()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DatabaseProviderSelector.Resolve(null), Is.EqualTo(DatabaseProviders.Sqlite));
            Assert.That(DatabaseProviderSelector.Resolve("sqlite"), Is.EqualTo(DatabaseProviders.Sqlite));
            Assert.That(DatabaseProviderSelector.Resolve("postgres"), Is.EqualTo(DatabaseProviders.Sqlite));
            Assert.That(DatabaseProviderSelector.Resolve("postgresql"), Is.EqualTo(DatabaseProviders.Postgresql));
            Assert.That(DatabaseProviderSelector.Resolve("POSTGRESQL"), Is.EqualTo(DatabaseProviders.Postgresql));
        });
    }

    /// <summary>PostgresAppDbContext é um AppDbContext com migrações próprias anotadas.</summary>
    [Test]
    public void T02_PostgresContext_MigracoesAnotadas()
    {
        Assert.That(typeof(PostgresAppDbContext).IsAssignableTo(typeof(AppDbContext)), Is.True);

        var migrations = typeof(PostgresAppDbContext).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes(
                    typeof(Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute), false)
                .Cast<Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute>()
                .Any(a => a.ContextType == typeof(PostgresAppDbContext)))
            .ToList();
        Assert.That(migrations.Count, Is.GreaterThanOrEqualTo(2),
            "esperava migration + model snapshot do Postgres");
    }

    // ---------------- StorageFactory ----------------

    /// <summary>Default resolve LocalFileStorage; s3 exige bucket.</summary>
    [Test]
    public void T03_StorageFactory_Selecao()
    {
        var local = StorageFactory.Create(Config(), _tempDir);
        Assert.That(local, Is.TypeOf<LocalFileStorage>());

        var s3 = StorageFactory.Create(Config(
            ("STORAGE_PROVIDER", "s3"),
            ("STORAGE_S3_BUCKET", "openwebui"),
            ("STORAGE_S3_ENDPOINT", "http://localhost:9000"),
            ("STORAGE_S3_ACCESS_KEY", "a"),
            ("STORAGE_S3_SECRET_KEY", "b")), _tempDir);
        Assert.That(s3, Is.TypeOf<S3FileStorage>());

        Assert.Throws<InvalidOperationException>(() =>
            StorageFactory.Create(Config(("STORAGE_PROVIDER", "s3")), _tempDir));
    }

    // ---------------- LocalFileStorage ----------------

    /// <summary>Roundtrip local: salva no layout legado, lê, apaga idempotente.</summary>
    [Test]
    public async Task T04_LocalStorage_Roundtrip()
    {
        var storage = new LocalFileStorage(_tempDir);
        var content = Encoding.UTF8.GetBytes("conteúdo do arquivo");

        var key = await storage.SaveAsync("user-1", "file-1", "doc.txt",
            new MemoryStream(content));
        Assert.That(key, Does.EndWith(Path.Combine("user-1", "file-1_doc.txt")));
        Assert.That(File.Exists(key), Is.True);

        await using (var stream = await storage.OpenReadAsync(key))
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            Assert.That(ms.ToArray(), Is.EqualTo(content));
        }

        await storage.DeleteAsync(key);
        Assert.That(File.Exists(key), Is.False);
        await storage.DeleteAsync(key); // idempotente

        Assert.That(await storage.PingAsync(), Is.True);
    }

    // ---------------- S3FileStorage (fake) ----------------

    private sealed class FakeS3Client : IS3Client
    {
        public readonly Dictionary<string, byte[]> Objects = new();

        public Task PutAsync(string bucket, string key, Stream content,
            string? contentType, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            Objects[$"{bucket}/{key}"] = ms.ToArray();
            return Task.CompletedTask;
        }

        public Task<Stream> GetAsync(string bucket, string key, CancellationToken ct) =>
            Objects.TryGetValue($"{bucket}/{key}", out var bytes)
                ? Task.FromResult<Stream>(new MemoryStream(bytes))
                : throw new FileNotFoundException(key);

        public Task DeleteAsync(string bucket, string key, CancellationToken ct)
        {
            Objects.Remove($"{bucket}/{key}");
            return Task.CompletedTask;
        }

        public Task<bool> PingAsync(string bucket, CancellationToken ct) =>
            Task.FromResult(true);
    }

    /// <summary>S3: chave {userId}/{id}_{name}, referência s3://bucket/key, roundtrip.</summary>
    [Test]
    public async Task T05_S3Storage_Roundtrip()
    {
        var s3 = new FakeS3Client();
        var storage = new S3FileStorage(s3, "openwebui");
        var content = Encoding.UTF8.GetBytes("binário s3");

        var reference = await storage.SaveAsync("u1", "f1", "img.png",
            new MemoryStream(content));
        Assert.That(reference, Is.EqualTo("s3://openwebui/u1/f1_img.png"));
        Assert.That(s3.Objects.Keys, Does.Contain("openwebui/u1/f1_img.png"));

        await using (var stream = await storage.OpenReadAsync(reference))
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            Assert.That(ms.ToArray(), Is.EqualTo(content));
        }

        await storage.DeleteAsync(reference);
        Assert.That(s3.Objects, Is.Empty);
        Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await storage.OpenReadAsync(reference));
        Assert.That(await storage.PingAsync(), Is.True);
    }

    // ---------------- Healthcheck ----------------

    /// <summary>/health responde com status do banco e provider de storage.</summary>
    [Test]
    public async Task T06_Health_VerificaDbEStorage()
    {
        var response = await _client.GetAsync("/health");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("\"status\":true"));
        Assert.That(body, Does.Contain("\"database\":\"up\""));
        Assert.That(body, Does.Contain("\"storage\":\"local\""));
    }
}
