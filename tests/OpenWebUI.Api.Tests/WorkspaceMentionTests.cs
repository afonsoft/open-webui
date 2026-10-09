using OpenWebUI.Infrastructure.ChatTools;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// RF-002 (SPEC-20261009-ide-mentions-tests): expansão dos chips @path em
/// blocos <c>&lt;file path="…"&gt;</c> no prompt — cap 16KB/arquivo, 64KB
/// total, binário/oversize → placeholder, fora do jail → drop silencioso.
/// </summary>
[TestFixture]
public class WorkspaceMentionTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Join(Path.GetTempPath(), $"mention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private string Write(string rel, string content)
    {
        var full = Path.Join(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return rel;
    }

    [Test]
    public async Task Build_ArquivoSimples_GeraBlocoFile()
    {
        Write("README.md", "hello mention");
        var result = await WorkspaceMentionContext.BuildAsync(_dir, ["README.md"]);
        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Contain("<file path=\"README.md\">"));
            Assert.That(result, Does.Contain("hello mention"));
            Assert.That(result, Does.Contain("</file>"));
        });
    }

    [Test]
    public async Task Build_ArquivoAcimaDe16KB_TruncaComNota()
    {
        Write("big.txt", new string('a', WorkspaceMentionContext.MaxFileChars + 500));
        var result = await WorkspaceMentionContext.BuildAsync(_dir, ["big.txt"]);
        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Contain("[… truncated]"));
            Assert.That(result!.Length, Is.LessThan(WorkspaceMentionContext.MaxFileChars + 300));
        });
    }

    [Test]
    public async Task Build_TotalAcimaDe64KB_ArquivosSeguintesViramPlaceholder()
    {
        Write("a.txt", new string('a', WorkspaceMentionContext.MaxFileChars));
        Write("b.txt", new string('b', WorkspaceMentionContext.MaxFileChars));
        Write("c.txt", new string('c', WorkspaceMentionContext.MaxFileChars));
        Write("d.txt", new string('d', WorkspaceMentionContext.MaxFileChars));
        Write("e.txt", new string('e', 100));

        var result = await WorkspaceMentionContext.BuildAsync(
            _dir, ["a.txt", "b.txt", "c.txt", "d.txt", "e.txt"]);
        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Contain("path=\"a.txt\""));
            Assert.That(result, Does.Contain("path=\"e.txt\""));
            Assert.That(result, Does.Contain("omitted").Or.Contain("truncated"),
                "o 5º arquivo deve estourar o cap total de 64KB");
            Assert.That(result!.Length, Is.LessThan(WorkspaceMentionContext.MaxTotalChars + 600));
        });
    }

    [Test]
    public async Task Build_Binario_PlaceholderSemConteudo()
    {
        var full = Path.Join(_dir, "logo.png");
        File.WriteAllBytes(full, [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02]);
        var result = await WorkspaceMentionContext.BuildAsync(_dir, ["logo.png"]);
        Assert.That(result, Does.Contain("[binary file — content omitted]"));
    }

    [Test]
    public async Task Build_ForaDoJail_EdiretorioEInexistente_SaoDescartados()
    {
        Write("ok.txt", "dentro");
        Directory.CreateDirectory(Path.Join(_dir, "subdir"));

        var result = await WorkspaceMentionContext.BuildAsync(
            _dir, ["../fora.txt", "subdir", "missing.txt", "ok.txt"]);
        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Not.Contain("fora.txt"), "fora do jail → drop silencioso");
            Assert.That(result, Does.Not.Contain("subdir"), "diretório → drop");
            Assert.That(result, Does.Not.Contain("missing"), "inexistente → drop");
            Assert.That(result, Does.Contain("dentro"));
        });
    }

    [Test]
    public async Task Build_PathComTraversal_SilentDrop()
    {
        var outside = Path.Join(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outside, "segredo");
        try
        {
            var rel = Path.GetRelativePath(_dir, outside);
            var result = await WorkspaceMentionContext.BuildAsync(_dir, [rel]);
            Assert.That(result, Is.Null.Or.Not.Contain("segredo"));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Test]
    public async Task Build_Dedupe_MesmoPathUmaVez()
    {
        Write("a.txt", "conteudo-a");
        var result = await WorkspaceMentionContext.BuildAsync(_dir, ["a.txt", "a.txt"]);
        var count = result!.Split("<file path=").Length - 1;
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task Build_PathComAspasEscapado_NaoQuebraXml()
    {
        Write("we\"ird.txt", "x");
        var result = await WorkspaceMentionContext.BuildAsync(_dir, ["we\"ird.txt"]);
        Assert.That(result, Does.Contain("we&quot;ird.txt"));
    }

    [Test]
    public async Task Build_SemArquivosElegiveis_Null()
    {
        var result = await WorkspaceMentionContext.BuildAsync(_dir, ["nada.txt", "../x"]);
        Assert.That(result, Is.Null);
    }
}
