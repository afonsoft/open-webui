using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura de <see cref="SkillDiscoveryService"/> (E16 S3+S4): scan de
/// skills/commands com precedência de raízes, frontmatter, dedupe por nome,
/// caps, cache/Invalidate e instruções de projeto (AGENTS/CLAUDE/cursor rules).
/// </summary>
[TestFixture]
public class SkillDiscoveryTests
{
    private string _dir = null!;
    private SkillDiscoveryService _svc = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Join(Path.GetTempPath(), $"skills-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _svc = new SkillDiscoveryService();
    }

    [TearDown]
    public void TearDown()
    {
        SkillDiscoveryService.Invalidate(_dir);
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private string WriteFile(string rel, string content)
    {
        var full = Path.Join(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Test]
    public void Scan_WorkdirVazio_DevolveListasVazias()
    {
        var scan = _svc.Scan(_dir);
        Assert.Multiple(() =>
        {
            Assert.That(scan.Skills, Is.Empty);
            Assert.That(scan.Commands, Is.Empty);
        });
    }

    [Test]
    public void Scan_AchaSkillMd_ComFrontmatter()
    {
        WriteFile(".claude/skills/minha-skill/SKILL.md",
            "---\nname: custom-name\ndescription: faz coisas\nagent: builder\nsubtask: true\n---\n\nCorpo da skill.");
        var scan = _svc.Scan(_dir);
        Assert.That(scan.Skills, Has.Count.EqualTo(1));
        var s = scan.Skills[0];
        Assert.Multiple(() =>
        {
            Assert.That(s.Name, Is.EqualTo("custom-name"));
            Assert.That(s.Description, Is.EqualTo("faz coisas"));
            Assert.That(s.Body, Is.EqualTo("Corpo da skill."));
            Assert.That(s.Agent, Is.EqualTo("builder"));
            Assert.That(s.Subtask, Is.True);
            Assert.That(s.Path, Is.EqualTo(".claude/skills/minha-skill/SKILL.md"));
        });
    }

    [Test]
    public void Scan_SkillSemFrontmatter_NomeDoDirEDescricaoDaPrimeiraLinha()
    {
        WriteFile("skills/minha/SKILL.md", "# Título da skill\n\nresto do corpo");
        var scan = _svc.Scan(_dir);
        Assert.That(scan.Skills, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(scan.Skills[0].Name, Is.EqualTo("minha"));
            Assert.That(scan.Skills[0].Description, Is.EqualTo("Título da skill"));
        });
    }

    [Test]
    public void Scan_PrecedenciaDeRaiz_PrimeiroNomeVence()
    {
        WriteFile(".agents/skills/mesma/SKILL.md", "---\ndescription: agents\n---\nA");
        WriteFile("skills/mesma/SKILL.md", "---\ndescription: skills\n---\nB");
        var scan = _svc.Scan(_dir);
        Assert.That(scan.Skills, Has.Count.EqualTo(1));
        Assert.That(scan.Skills[0].Description, Is.EqualTo("agents"),
            ".agents/skills tem precedência sobre skills/");
    }

    [Test]
    public void Scan_AchaCommands_PorBasename()
    {
        WriteFile(".opencode/command/build.md", "roda o build");
        WriteFile(".claude/commands/deploy.md", "faz deploy");
        var scan = _svc.Scan(_dir);
        Assert.Multiple(() =>
        {
            Assert.That(scan.Commands, Has.Count.EqualTo(2));
            Assert.That(scan.Commands.Select(c => c.Name), Is.EquivalentTo(new[] { "build", "deploy" }));
        });
    }

    [Test]
    public void Scan_PulaArquivoGrandeEVazio()
    {
        WriteFile(".claude/skills/vazia/SKILL.md", "");
        WriteFile(".claude/skills/grande/SKILL.md", new string('x', SkillDiscoveryService.MaxFileBytes + 1));
        WriteFile(".claude/skills/ok/SKILL.md", "valida");
        var scan = _svc.Scan(_dir);
        Assert.Multiple(() =>
        {
            Assert.That(scan.Skills, Has.Count.EqualTo(1));
            Assert.That(scan.Skills[0].Name, Is.EqualTo("ok"));
        });
    }

    [Test]
    public void Scan_IgnoraSkipDirs()
    {
        WriteFile("skills/escondida/node_modules/x/SKILL.md", "não deve aparecer");
        WriteFile("skills/visivel/SKILL.md", "aparece");
        var scan = _svc.Scan(_dir);
        Assert.That(scan.Skills.Select(s => s.Name), Is.EqualTo(new[] { "visivel" }));
    }

    [Test]
    public void Scan_CacheInvalidadoPorInvalidate()
    {
        WriteFile("skills/a/SKILL.md", "primeira");
        Assert.That(_svc.Scan(_dir).Skills, Has.Count.EqualTo(1));
        WriteFile("skills/b/SKILL.md", "segunda");
        Assert.That(_svc.Scan(_dir).Skills, Has.Count.EqualTo(1), "cache segura o scan novo");
        SkillDiscoveryService.Invalidate(_dir);
        Assert.That(_svc.Scan(_dir).Skills, Has.Count.EqualTo(2));
    }

    [Test]
    public void Find_SkillPrimeiroDepoisCommand_CaseInsensitive()
    {
        WriteFile("skills/dup/SKILL.md", "skill body");
        WriteFile(".claude/commands/dup.md", "command body");
        var scan = _svc.Scan(_dir);
        Assert.Multiple(() =>
        {
            Assert.That(_svc.Find(scan, "DUP")!.Body, Is.EqualTo("skill body"));
            Assert.That(_svc.Find(scan, "inexistente"), Is.Null);
        });
    }

    [Test]
    public void LoadProjectInstructions_OrdemArquivos()
    {
        WriteFile("README.md", "readme aqui");
        WriteFile("AGENTS.md", "agents primeiro");
        var text = SkillDiscoveryService.LoadProjectInstructions(_dir);
        Assert.That(text, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("--- AGENTS.md ---"));
            Assert.That(text, Does.Contain("agents primeiro"));
            Assert.That(text, Does.Contain("--- README.md ---"));
            Assert.That(text, Does.Contain("readme aqui"));
            Assert.That(text!.IndexOf("AGENTS.md"), Is.LessThan(text.IndexOf("README.md")));
        });
    }

    [Test]
    public void LoadProjectInstructions_CursorRulesNoMeio()
    {
        WriteFile("CLAUDE.md", "claude");
        WriteFile(".cursor/rules/r1.md", "regra um");
        WriteFile(".cursor/rules/r2.md", "regra dois");
        var text = SkillDiscoveryService.LoadProjectInstructions(_dir);
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("regra um"));
            Assert.That(text, Does.Contain("regra dois"));
            Assert.That(text!.IndexOf("regra um"), Is.LessThan(text.IndexOf("regra dois")));
        });
    }

    [Test]
    public void LoadProjectInstructions_SemArquivos_Null()
    {
        Assert.That(SkillDiscoveryService.LoadProjectInstructions(_dir), Is.Null);
    }
}
