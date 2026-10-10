using System.ComponentModel;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Regressão do data root em container (500 em POST /api/v1/workspace/repo/open):
/// o app resolvia dados sob <c>{ContentRootPath}/data</c> — inacessível quando
/// /app é root-owned no container — enquanto o volume persistente vive em
/// <c>/data</c>. <c>DATA_ROOT</c> sobrepõe a raiz; o Dockerfile define
/// <c>DATA_ROOT=/data</c> (PR #264).
/// </summary>
[NonParallelizable] // muta DATA_ROOT (variável de processo)
[TestFixture, IsolateEnvironment]
public class DataPathsTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), $"owui-datapaths-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("DATA_ROOT", null);
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    [Test]
    public void Root_SemEnv_UsaContentRootData()
    {
        Environment.SetEnvironmentVariable("DATA_ROOT", null);
        Assert.That(DataPaths.Root(_root), Is.EqualTo(Path.Join(_root, "data")));
    }

    [Test]
    public void Root_ComEnv_SobrepoeContentRoot()
    {
        var dataDir = Path.Join(Path.GetTempPath(), $"owui-datapaths-vol-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable("DATA_ROOT", dataDir);
            Assert.That(DataPaths.Root(_root), Is.EqualTo(dataDir));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATA_ROOT", null);
        }
    }

    [Test]
    public async Task Repo_Open_ComEnv_ClonaDentroDoDataDir()
    {
        // Cenário Docker: ContentRootPath (/app) não é gravável; o volume
        // persistente é apontado por DATA_ROOT e o clone deve pousar lá.
        var dataDir = Path.Join(Path.GetTempPath(), $"owui-datapaths-ws-{Guid.NewGuid():N}");
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Join(_root, "t.db")}").Options);
        await DatabaseMigrator.MigrateAsync(db);
        var cache = TestCache.Create();
        var repos = new WorkspaceRepoService(
            new ConfigService(db, cache), new StubEnv(_root));
        var origin = CriarOrigem("main");
        try
        {
            Environment.SetEnvironmentVariable("DATA_ROOT", dataDir);
            var (binding, error) = await repos.OpenAsync(
                "u1", "a/b", "main", origin, null, default);
            Assert.Multiple(() =>
            {
                Assert.That(error, Is.Null);
                Assert.That(binding, Is.Not.Null);
                Assert.That(binding!.Dir, Does.StartWith("repos/"));
                Assert.That(Directory.Exists(Path.Join(
                    dataDir, "workspaces", "u1", binding.Dir, ".git")), Is.True);
                Assert.That(Directory.Exists(Path.Join(_root, "data")), Is.False);
            });
        }
        catch (Win32Exception) { Assert.Ignore("git indisponível neste ambiente."); }
        catch (InvalidOperationException) { Assert.Ignore("git indisponível neste ambiente."); }
        finally
        {
            Environment.SetEnvironmentVariable("DATA_ROOT", null);
            db.Dispose();
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
            try { Directory.Delete(origin, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Cria um repo local (remote <c>file://</c>) com 1 commit.</summary>
    private static string CriarOrigem(string branch)
    {
        var origin = Path.Join(Path.GetTempPath(), $"owui-origin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(origin);
        Git(origin, "init", "-b", branch);
        File.WriteAllText(Path.Join(origin, "readme.md"), "oi\n");
        Git(origin, "-c", "user.email=t@t", "-c", "user.name=t", "add", ".");
        Git(origin, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-m", "base");
        return origin;
    }

    private static string? Git(string? workdir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (workdir is not null)
        {
            psi.WorkingDirectory = workdir;
        }
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi);
        process!.WaitForExit(15000);
        return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : null;
    }

    private sealed class StubEnv(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
