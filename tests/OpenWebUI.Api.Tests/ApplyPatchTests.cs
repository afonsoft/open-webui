using OpenWebUI.Infrastructure.ChatTools;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura de <see cref="ApplyPatch"/> (RF-003 worktree-format-hooks):
/// parse do formato OpenAI (add/update/delete/move), validações de forma
/// e ApplyHunks com casamento exato/trim-end/trim, ambiguidade e hunk
/// puro de adição.
/// </summary>
[TestFixture]
public class ApplyPatchTests
{
    // ---------- Parse ----------

    [Test]
    public void Parse_SemBeginPatch_Erro()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ApplyPatch.Parse("qualquer coisa", out var err), Is.Null);
            Assert.That(err, Does.Contain("Begin Patch"));
            Assert.That(ApplyPatch.Parse("", out var err2), Is.Null);
            Assert.That(err2, Does.Contain("Begin Patch"));
        });
    }

    [Test]
    public void Parse_SemEndPatch_Erro()
    {
        var ops = ApplyPatch.Parse("*** Begin Patch\n*** Add File: a.txt\n+x", out var err);
        Assert.Multiple(() =>
        {
            Assert.That(ops, Is.Null);
            Assert.That(err, Does.Contain("End Patch"));
        });
    }

    [Test]
    public void Parse_VazioSemOps_Erro()
    {
        var ops = ApplyPatch.Parse("*** Begin Patch\n*** End Patch\n", out var err);
        Assert.Multiple(() =>
        {
            Assert.That(ops, Is.Null);
            Assert.That(err, Does.Contain("vazio"));
        });
    }

    [Test]
    public void Parse_AddFile_ConteudoMais()
    {
        var ops = ApplyPatch.Parse(
            "*** Begin Patch\n*** Add File: dir/novo.cs\n+linha1\n+linha2\n*** End Patch",
            out var err);
        Assert.Multiple(() =>
        {
            Assert.That(err, Is.Empty);
            Assert.That(ops, Has.Count.EqualTo(1));
            Assert.That(ops![0].Kind, Is.EqualTo("add"));
            Assert.That(ops[0].Path, Is.EqualTo("dir/novo.cs"));
            Assert.That(ops[0].AddLines, Is.EqualTo(new[] { "linha1", "linha2" }));
        });
    }

    [Test]
    public void Parse_AddFile_LinhaSemMais_Erro()
    {
        var ops = ApplyPatch.Parse(
            "*** Begin Patch\n*** Add File: a.txt\nsem-mais\n*** End Patch", out var err);
        Assert.Multiple(() =>
        {
            Assert.That(ops, Is.Null);
            Assert.That(err, Does.Contain("'+'"));
        });
    }

    [Test]
    public void Parse_DeleteFile()
    {
        var ops = ApplyPatch.Parse(
            "*** Begin Patch\n*** Delete File: velho.txt\n*** End Patch", out var err);
        Assert.Multiple(() =>
        {
            Assert.That(ops, Has.Count.EqualTo(1));
            Assert.That(ops![0].Kind, Is.EqualTo("delete"));
            Assert.That(ops[0].Path, Is.EqualTo("velho.txt"));
        });
    }

    [Test]
    public void Parse_UpdateFile_HunksESeparador()
    {
        var ops = ApplyPatch.Parse(
            "*** Begin Patch\n*** Update File: f.cs\n" +
            " ctx1\n-velho\n+novo\n@@\n ctx2\n-outro\n+outro2\n*** End Patch",
            out var err);
        Assert.Multiple(() =>
        {
            Assert.That(err, Is.Empty);
            Assert.That(ops, Has.Count.EqualTo(1));
            Assert.That(ops![0].Kind, Is.EqualTo("update"));
            Assert.That(ops[0].Hunks, Has.Count.EqualTo(2));
            Assert.That(ops[0].Hunks![0].Expected, Is.EqualTo(new[] { "ctx1", "velho" }));
            Assert.That(ops[0].Hunks![0].Replacement, Is.EqualTo(new[] { "ctx1", "novo" }));
            Assert.That(ops[0].Hunks![1].Expected, Is.EqualTo(new[] { "ctx2", "outro" }));
            Assert.That(ops[0].Hunks![1].Replacement, Is.EqualTo(new[] { "ctx2", "outro2" }));
        });
    }

    [Test]
    public void Parse_UpdateFile_MoveToSemHunks()
    {
        var ops = ApplyPatch.Parse(
            "*** Begin Patch\n*** Update File: a.cs\n*** Move to: b.cs\n*** End Patch",
            out var err);
        Assert.Multiple(() =>
        {
            Assert.That(err, Is.Empty);
            Assert.That(ops![0].MoveTo, Is.EqualTo("b.cs"));
        });
    }

    [Test]
    public void Parse_UpdateFile_SemHunksNemMove_Erro()
    {
        var ops = ApplyPatch.Parse(
            "*** Begin Patch\n*** Update File: a.cs\n*** End Patch", out var err);
        Assert.Multiple(() =>
        {
            Assert.That(ops, Is.Null);
            Assert.That(err, Does.Contain("nenhum hunk"));
        });
    }

    [Test]
    public void Parse_UpdateFile_LinhaInvalidaNoHunk_Erro()
    {
        var ops = ApplyPatch.Parse(
            "*** Begin Patch\n*** Update File: a.cs\n?estranho\n*** End Patch", out var err);
        Assert.Multiple(() =>
        {
            Assert.That(ops, Is.Null);
            Assert.That(err, Does.Contain("linha inválida"));
        });
    }

    [Test]
    public void Parse_LinhaInesperada_Erro()
    {
        var ops = ApplyPatch.Parse(
            "*** Begin Patch\n*** Wat: x\n*** End Patch", out var err);
        Assert.Multiple(() =>
        {
            Assert.That(ops, Is.Null);
            Assert.That(err, Does.Contain("inesperada"));
        });
    }

    [Test]
    public void Parse_MultiOpsEEndOfFile_TodasColetadas()
    {
        var ops = ApplyPatch.Parse(
            "*** Begin Patch\n*** Add File: a\n+x\n*** End of File\n" +
            "*** Delete File: b\n*** Update File: c\n y\n-q\n+z\n*** End Patch",
            out var err);
        Assert.Multiple(() =>
        {
            Assert.That(ops, Has.Count.EqualTo(3));
            Assert.That(ops![0].Kind, Is.EqualTo("add"));
            Assert.That(ops[1].Kind, Is.EqualTo("delete"));
            Assert.That(ops[2].Kind, Is.EqualTo("update"));
        });
    }

    // ---------- ApplyHunks ----------

    [Test]
    public void ApplyHunks_SubstituiBlocoExato()
    {
        var hunk = new PatchHunk(["b", "c"], ["b", "C", "c2"]);
        var result = ApplyPatch.ApplyHunks("a\nb\nc\nd\n", [hunk], out var err);
        Assert.Multiple(() =>
        {
            Assert.That(err, Is.Empty);
            Assert.That(result, Is.EqualTo("a\nb\nC\nc2\nd\n"));
        });
    }

    [Test]
    public void ApplyHunks_CasaComTrimEnd()
    {
        // Arquivo tem espaços finais; o hunk veio sem — casa por trim-end.
        var hunk = new PatchHunk(["b"], ["B"]);
        var result = ApplyPatch.ApplyHunks("a\nb   \nc\n", [hunk], out var err);
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo("a\nB\nc\n"));
            Assert.That(err, Is.Empty);
        });
    }

    [Test]
    public void ApplyHunks_CasaComTrimTotal()
    {
        // Arquivo indentado; hunk com indentação diferente — casa por trim.
        var hunk = new PatchHunk(["b"], ["B"]);
        var result = ApplyPatch.ApplyHunks("a\n    b\nc\n", [hunk], out var err);
        Assert.That(result, Is.EqualTo("a\nB\nc\n"));
    }

    [Test]
    public void ApplyHunks_NaoEncontrado_ErroComDetalhe()
    {
        var hunk = new PatchHunk(["inexistente"], ["x"]);
        var result = ApplyPatch.ApplyHunks("a\nb\n", [hunk], out var err);
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Null);
            Assert.That(err, Does.Contain("não encontrado"));
            Assert.That(err, Does.Contain("0 ocorrências"));
        });
    }

    [Test]
    public void ApplyHunks_Ambiguo_Erro()
    {
        var hunk = new PatchHunk(["b"], ["B"]);
        var result = ApplyPatch.ApplyHunks("a\nb\nc\nb\n", [hunk], out var err);
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Null);
            Assert.That(err, Does.Contain("ambíguo"));
        });
    }

    [Test]
    public void ApplyHunks_PuroAdicao_AnexaAntesDoNewlineFinal()
    {
        var hunk = new PatchHunk([], ["nova-linha"]);
        var result = ApplyPatch.ApplyHunks("a\nb\n", [hunk], out var err);
        Assert.Multiple(() =>
        {
            Assert.That(err, Is.Empty);
            Assert.That(result, Is.EqualTo("a\nb\nnova-linha\n"));
        });
    }

    [Test]
    public void ApplyHunks_PuroAdicao_SemNewlineFinal()
    {
        var hunk = new PatchHunk([], ["fim"]);
        var result = ApplyPatch.ApplyHunks("a\nb", [hunk], out var err);
        Assert.That(result, Is.EqualTo("a\nb\nfim"));
    }

    [Test]
    public void ApplyHunks_DoisHunksSequenciais()
    {
        var result = ApplyPatch.ApplyHunks("a\nb\nc\nd\n", [
            new PatchHunk(["b"], ["B"]),
            new PatchHunk(["d"], ["D"]),
        ], out var err);
        Assert.That(result, Is.EqualTo("a\nB\nc\nD\n"));
    }
}
