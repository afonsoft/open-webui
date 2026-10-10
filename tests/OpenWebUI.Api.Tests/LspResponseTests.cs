using System.Text.Json;
using OpenWebUI.Infrastructure.Lsp;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura de <see cref="LspResponse"/>: flatten de Location/LocationLink,
/// documentSymbol hierárquico e plano, workspace/symbol, hover (MarkupContent/
/// MarkedString/array), nomes de SymbolKind e RelPath com fallback.
/// </summary>
[TestFixture]
public class LspResponseEdgeTests
{
    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement;

    // ---------- FlattenLocations ----------

    [Test]
    public void Locations_Null_ou_Undefined_DevolveVazio()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LspResponse.FlattenLocations(Parse("null"), 10), Is.Empty);
            Assert.That(LspResponse.FlattenLocations(default, 10), Is.Empty);
        });
    }

    [Test]
    public void Locations_ObjetoUnico_ViraLista()
    {
        var result = Parse(
            "{\"uri\":\"file:///w/a.cs\",\"range\":{\"start\":{\"line\":3,\"character\":7},\"end\":{\"line\":3,\"character\":12}}}");
        var locs = LspResponse.FlattenLocations(result, 10);
        Assert.That(locs, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(locs[0].Path, Is.EqualTo("/w/a.cs"));
            Assert.That(locs[0].Line, Is.EqualTo(3));
            Assert.That(locs[0].Col, Is.EqualTo(7));
            Assert.That(locs[0].EndLine, Is.EqualTo(3));
            Assert.That(locs[0].EndCol, Is.EqualTo(12));
        });
    }

    [Test]
    public void Locations_LocationLink_UsaTargetUriETargetRange()
    {
        var result = Parse(
            "[{\"targetUri\":\"file:///w/b.cs\",\"targetRange\":{\"start\":{\"line\":1,\"character\":0},\"end\":{\"line\":2,\"character\":4}}}," +
            "{\"targetUri\":\"file:///w/c.cs\",\"targetSelectionRange\":{\"start\":{\"line\":5,\"character\":2}}}]");
        var locs = LspResponse.FlattenLocations(result, 10);
        Assert.That(locs, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(locs[0].Path, Is.EqualTo("/w/b.cs"));
            Assert.That(locs[0].EndLine, Is.EqualTo(2));
            // Sem "end" o range colapsa no start.
            Assert.That(locs[1].Path, Is.EqualTo("/w/c.cs"));
            Assert.That(locs[1].EndCol, Is.EqualTo(2));
        });
    }

    [Test]
    public void Locations_EntradasInvalidas_SaoIgnoradasEAplicamCap()
    {
        var result = Parse(
            "[42,{\"uri\":\"https://nao-file/x\"},\"texto\"," +
            "{\"uri\":\"file:///w/a.cs\",\"range\":{\"start\":{\"line\":0,\"character\":0}}}," +
            "{\"uri\":\"file:///w/b.cs\",\"range\":{\"start\":{\"line\":1,\"character\":1}}}]");
        var locs = LspResponse.FlattenLocations(result, 1);
        Assert.Multiple(() =>
        {
            Assert.That(locs, Has.Count.EqualTo(1), "cap=1 limita");
            Assert.That(locs[0].Path, Is.EqualTo("/w/a.cs"));
        });
    }

    // ---------- FormatLocations ----------

    [Test]
    public void FormatLocations_Vazio_MensagemNenhuma()
    {
        Assert.That(LspResponse.FormatLocations(Parse("null"), "/w", 10),
            Is.EqualTo("Nenhuma localização."));
    }

    [Test]
    public void FormatLocations_FormataLinhaCol1Based()
    {
        var result = Parse(
            "[{\"uri\":\"file:///tmp/w/sub/a.cs\",\"range\":{\"start\":{\"line\":4,\"character\":9}}}]");
        var text = LspResponse.FormatLocations(result, "/tmp/w", 10);
        Assert.That(text, Is.EqualTo("sub/a.cs:5:10"));
    }

    // ---------- FlattenSymbols (documentSymbol) ----------

    [Test]
    public void Symbols_NaoArray_DevolveVazio()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LspResponse.FlattenSymbols(Parse("{}"), "/w/a.cs", 10), Is.Empty);
            Assert.That(LspResponse.FlattenSymbols(Parse("null"), "/w/a.cs", 10), Is.Empty);
        });
    }

    [Test]
    public void Symbols_DocumentSymbolHierarquico_DesceChildrenComContainer()
    {
        var result = Parse(
            "[{\"name\":\"MinhaClasse\",\"kind\":5," +
            "\"range\":{\"start\":{\"line\":0,\"character\":0}}," +
            "\"selectionRange\":{\"start\":{\"line\":1,\"character\":4}}," +
            "\"children\":[" +
            "{\"name\":\"Metodo\",\"kind\":6,\"selectionRange\":{\"start\":{\"line\":10,\"character\":8}}}," +
            "{\"kind\":6}]}," +
            "{\"kind\":12}]");
        var syms = LspResponse.FlattenSymbols(result, "/w/a.cs", 10);
        Assert.That(syms, Has.Count.EqualTo(2), "filho sem name e símbolo sem name são pulados");
        Assert.Multiple(() =>
        {
            Assert.That(syms[0].Name, Is.EqualTo("MinhaClasse"));
            Assert.That(syms[0].Kind, Is.EqualTo(5));
            Assert.That(syms[0].Path, Is.EqualTo("/w/a.cs"));
            Assert.That(syms[0].Line, Is.EqualTo(1));
            Assert.That(syms[0].Col, Is.EqualTo(4));
            Assert.That(syms[1].Name, Is.EqualTo("Metodo"));
            Assert.That(syms[1].Container, Is.EqualTo("MinhaClasse"));
            Assert.That(syms[1].Line, Is.EqualTo(10));
        });
    }

    [Test]
    public void Symbols_SymbolInformation_UsaLocationUriERange()
    {
        var result = Parse(
            "[{\"name\":\"Foo\",\"kind\":12,\"containerName\":\"Mod\"," +
            "\"location\":{\"uri\":\"file:///w/outro.cs\"," +
            "\"range\":{\"start\":{\"line\":2,\"character\":3}}}}]");
        var syms = LspResponse.FlattenSymbols(result, "/w/a.cs", 10);
        Assert.That(syms, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(syms[0].Path, Is.EqualTo("/w/outro.cs"));
            Assert.That(syms[0].Line, Is.EqualTo(2));
            Assert.That(syms[0].Col, Is.EqualTo(3));
            Assert.That(syms[0].Container, Is.EqualTo("Mod"));
        });
    }

    [Test]
    public void Symbols_Cap_TruncaRespeitandoHierarquia()
    {
        var result = Parse(
            "[{\"name\":\"A\",\"kind\":5,\"children\":[" +
            "{\"name\":\"B\",\"kind\":6},{\"name\":\"C\",\"kind\":6}]}," +
            "{\"name\":\"D\",\"kind\":5}]");
        var syms = LspResponse.FlattenSymbols(result, "/w/a.cs", 2);
        Assert.Multiple(() =>
        {
            Assert.That(syms, Has.Count.EqualTo(2));
            Assert.That(syms[0].Name, Is.EqualTo("A"));
            Assert.That(syms[1].Name, Is.EqualTo("B"));
        });
    }

    // ---------- FlattenWorkspaceSymbols ----------

    [Test]
    public void WorkspaceSymbols_PulaInvalidosEMantemRelPath()
    {
        var result = Parse(
            "[{\"name\":\"Ok\",\"kind\":6," +
            "\"location\":{\"uri\":\"file:///tmp/w/src/x.cs\"," +
            "\"range\":{\"start\":{\"line\":7,\"character\":1}}}}," +
            "{\"name\":\"SemUri\",\"kind\":6,\"location\":{}}," +
            "{\"sem\":\"name\",\"kind\":6,\"location\":{\"uri\":\"file:///tmp/w/y.cs\"}}," +
            "42,{}]");
        var syms = LspResponse.FlattenWorkspaceSymbols(result, "/tmp/w", 10);
        Assert.That(syms, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(syms[0].Name, Is.EqualTo("Ok"));
            Assert.That(syms[0].Path, Is.EqualTo("src/x.cs"));
            Assert.That(syms[0].Line, Is.EqualTo(7));
            Assert.That(syms[0].Kind, Is.EqualTo(6));
        });
    }

    [Test]
    public void WorkspaceSymbols_UriNaoFile_EContornada()
    {
        var result = Parse(
            "[{\"name\":\"X\",\"kind\":5,\"location\":{\"uri\":\"https://h/x\",\"range\":{}}}]");
        Assert.That(LspResponse.FlattenWorkspaceSymbols(result, "/w", 10), Is.Empty);
    }

    // ---------- HoverText ----------

    [Test]
    public void Hover_MarkupContent_LeValue()
    {
        var result = Parse(
            "{\"contents\":{\"kind\":\"markdown\",\"value\":\"**doc** aqui\"}}");
        Assert.That(LspResponse.HoverText(result), Is.EqualTo("**doc** aqui"));
    }

    [Test]
    public void Hover_MarkedStringEArray_Concatena()
    {
        var result = Parse(
            "{\"contents\":[{\"language\":\"csharp\",\"value\":\"int x\"},\"plain\"]}");
        Assert.That(LspResponse.HoverText(result), Is.EqualTo("int x\nplain"));
    }

    [Test]
    public void Hover_StringSimples_EFormatosVazios()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LspResponse.HoverText(Parse("{\"contents\":\"ola\"}")), Is.EqualTo("ola"));
            Assert.That(LspResponse.HoverText(Parse("{\"contents\":\"  \"}")), Is.Null);
            Assert.That(LspResponse.HoverText(Parse("{}")), Is.Null);
            Assert.That(LspResponse.HoverText(Parse("[]")), Is.Null);
            Assert.That(LspResponse.HoverText(Parse("{\"contents\":42}")), Is.Null);
        });
    }

    // ---------- SymbolKindName ----------

    [Test]
    public void SymbolKindName_MapeiaConhecidosEFallback()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LspResponse.SymbolKindName(5), Is.EqualTo("class"));
            Assert.That(LspResponse.SymbolKindName(6), Is.EqualTo("method"));
            Assert.That(LspResponse.SymbolKindName(12), Is.EqualTo("function"));
            Assert.That(LspResponse.SymbolKindName(26), Is.EqualTo("typeparam"));
            Assert.That(LspResponse.SymbolKindName(0), Is.EqualTo("symbol"));
            Assert.That(LspResponse.SymbolKindName(99), Is.EqualTo("symbol"));
            Assert.That(LspResponse.SymbolKindName(-1), Is.EqualTo("symbol"));
        });
    }

    // ---------- RelPath ----------

    [Test]
    public void RelPath_RelativoEDistante()
    {
        var workdir = Path.Join(Path.GetTempPath(), $"rel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workdir);
        try
        {
            var dentro = Path.Join(workdir, "sub", "a.cs");
            Assert.Multiple(() =>
            {
                Assert.That(LspResponse.RelPath(workdir, dentro), Is.EqualTo("sub/a.cs"));
                Assert.That(LspResponse.RelPath(workdir, "/x/y.cs"),
                    Is.EqualTo(Path.GetRelativePath(workdir, "/x/y.cs")));
            });
        }
        finally
        {
            Directory.Delete(workdir, recursive: true);
        }
    }
}
