using System.Collections.Concurrent;
using System.Text;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>Entrada descoberta no repositório (skill ou command markdown).</summary>
public sealed record RepoEntry(
    string Name, string Description, string Path, string Body,
    string? Agent = null, string? Model = null, bool Subtask = false);

/// <summary>Resultado do scan: skills + commands já deduplicados por nome.</summary>
public sealed record RepoScanResult(
    IReadOnlyList<RepoEntry> Skills, IReadOnlyList<RepoEntry> Commands);

/// <summary>
/// Descobre skills (<c>SKILL.md</c>) e commands markdown do repositório
/// vinculado — equivalente ao <c>src/skill/discovery.ts</c> e
/// <c>src/command/index.ts</c> do opencode
/// (SPEC-20261009-repo-skills-slash-commands, E16 S3+S4).
/// Raízes varridas (precedência, primeiro nome vence):
/// skills — <c>.claude/skills</c> &gt; <c>.agents/skills</c> &gt;
/// <c>.devin/skills</c> &gt; <c>.opencode/skill[s]</c> &gt; <c>skill[s]/</c>;
/// commands — <c>.opencode/command</c>, <c>.claude/commands</c>,
/// <c>.agents/commands</c>. Cache em memória com TTL de 60s, invalidado em
/// rebind/troca de branch via <see cref="Invalidate"/>.
/// Somente leitura: o conteúdo é markdown injetado como contexto — nunca executado.
/// </summary>
public sealed class SkillDiscoveryService
{
    /// <summary>Máximo de skills e de commands retornados por scan.</summary>
    public const int MaxItems = 50;

    /// <summary>Teto por arquivo lido (SKILL.md ou command .md).</summary>
    public const int MaxFileBytes = 64 * 1024;

    /// <summary>Teto total das instruções de projeto injetadas no system prompt.</summary>
    public const int MaxInstructionsBytes = 32 * 1024;

    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, RepoScanResult Result)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] SkillRoots =
    [
        ".claude/skills", ".agents/skills", ".devin/skills",
        ".opencode/skill", ".opencode/skills", "skill", "skills",
    ];

    private static readonly string[] CommandRoots =
    [
        ".opencode/command", ".claude/commands", ".agents/commands",
    ];

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules",
    };

    /// <summary>Invalida o cache de um workdir (rebind ou troca de branch).</summary>
    public static void Invalidate(string workdir) => Cache.TryRemove(workdir, out _);

    /// <summary>Scan do workdir (com cache TTL 60s). Nunca lança por arquivo ruim.</summary>
    public RepoScanResult Scan(string workdir)
    {
        if (Cache.TryGetValue(workdir, out var hit) && DateTimeOffset.UtcNow - hit.At < Ttl)
        {
            return hit.Result;
        }

        var result = new RepoScanResult(
            ScanSkills(workdir), ScanCommands(workdir));
        Cache[workdir] = (DateTimeOffset.UtcNow, result);
        return result;
    }

    /// <summary>Busca uma entrada por nome (skill primeiro, depois command).</summary>
    public RepoEntry? Find(RepoScanResult scan, string name) =>
        scan.Skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? scan.Commands.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Lê os arquivos de instrução do projeto na ordem AGENTS.md → CLAUDE.md →
    /// <c>.cursor/rules/*.md</c> → README.md, até <see cref="MaxInstructionsBytes"/>.
    /// Retorna <c>null</c> quando nenhum existe.
    /// </summary>
    public static string? LoadProjectInstructions(string workdir)
    {
        var sb = new StringBuilder();
        var budget = MaxInstructionsBytes;
        foreach (var rel in new[] { "AGENTS.md", "CLAUDE.md" })
        {
            budget -= AppendFile(sb, Path.Join(workdir, rel), rel, budget);
        }

        var rulesDir = Path.Join(workdir, ".cursor", "rules");
        if (Directory.Exists(rulesDir) && !IsReparsePoint(rulesDir))
        {
            foreach (var file in Directory.EnumerateFiles(rulesDir, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (budget <= 0)
                {
                    break;
                }

                budget -= AppendFile(sb, file, Relative(workdir, file), budget);
            }
        }

        if (budget > 0)
        {
            AppendFile(sb, Path.Join(workdir, "README.md"), "README.md", budget);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static IReadOnlyList<RepoEntry> ScanSkills(string workdir) =>
        ScanRoots(workdir, SkillRoots, "SKILL.md", filePerEntry: true);

    private static IReadOnlyList<RepoEntry> ScanCommands(string workdir) =>
        ScanRoots(workdir, CommandRoots, "*.md", filePerEntry: false);

    /// <summary>
    /// Varre as raízes em ordem de precedência. <paramref name="filePerEntry"/>:
    /// true = cada arquivo é SKILL.md e o nome vem do diretório-pai;
    /// false = cada *.md é um command cujo nome vem do basename do arquivo.
    /// </summary>
    private static List<RepoEntry> ScanRoots(
        string workdir, string[] roots, string pattern, bool filePerEntry)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<RepoEntry>();
        var visited = 0;
        foreach (var root in roots)
        {
            var dir = Path.Join(workdir, root.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir) || IsReparsePoint(dir))
            {
                continue;
            }

            foreach (var file in EnumerateSafe(dir, pattern, ref visited))
            {
                if (items.Count >= MaxItems)
                {
                    return items;
                }

                var entry = TryParse(workdir, file, filePerEntry);
                if (entry is not null && seen.Add(entry.Name))
                {
                    items.Add(entry);
                }
            }
        }

        return items;
    }

    /// <summary>Enumera arquivos recursivamente sem seguir symlink/reparse point.</summary>
    private static List<string> EnumerateSafe(string dir, string pattern, ref int visited)
    {
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(dir);
        while (pending.Count > 0 && visited < 400)
        {
            var current = pending.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(current, pattern))
                {
                    visited++;
                    found.Add(f);
                }

                foreach (var sub in Directory.EnumerateDirectories(current))
                {
                    if (!IsReparsePoint(sub) && !SkipDirs.Contains(Path.GetFileName(sub)))
                    {
                        pending.Push(sub);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return found;
    }

    /// <summary>Parse de um arquivo: frontmatter YAML mínimo + corpo (≤64KB).</summary>
    private static RepoEntry? TryParse(string workdir, string file, bool filePerEntry)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(file);
            if (!info.Exists || info.Length == 0 || info.Length > MaxFileBytes)
            {
                return null;
            }
        }
        catch (IOException) { return null; }

        string raw;
        try
        {
            raw = File.ReadAllText(file);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        var fm = ParseFrontmatter(raw, out var body);
        var fallback = filePerEntry
            ? Path.GetFileName(Path.GetDirectoryName(file)) ?? info.Name
            : Path.GetFileNameWithoutExtension(file);
        var name = fm.GetValueOrDefault("name") ?? fallback;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var description = fm.GetValueOrDefault("description")
            ?? FirstLine(body)
            ?? string.Empty;
        return new RepoEntry(
            name.Trim(), description.Trim(), Relative(workdir, file), body.Trim(),
            Agent: fm.GetValueOrDefault("agent"),
            Model: fm.GetValueOrDefault("model"),
            Subtask: fm.GetValueOrDefault("subtask") is { } s
                && (s.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("agent", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Frontmatter mínimo: bloco <c>---\nkey: value\n---</c> no topo.</summary>
    private static Dictionary<string, string> ParseFrontmatter(string raw, out string body)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        body = raw;
        if (!raw.StartsWith("---", StringComparison.Ordinal))
        {
            return map;
        }

        var firstNl = raw.IndexOf('\n');
        if (firstNl < 0 || raw[..firstNl].Trim() != "---")
        {
            return map;
        }

        var end = raw.IndexOf("\n---", firstNl + 1, StringComparison.Ordinal);
        if (end < 0)
        {
            return map;
        }

        foreach (var line in raw[(firstNl + 1)..end].Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim().Trim('"', '\'');
            if (key.Length > 0 && value.Length > 0)
            {
                map[key] = value;
            }
        }

        var rest = raw[(end + 4)..];
        var nl = rest.IndexOf('\n');
        body = nl >= 0 ? rest[(nl + 1)..] : string.Empty;
        return map;
    }

    private static int AppendFile(StringBuilder sb, string full, string rel, int budget)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(full);
            if (!info.Exists || info.Length == 0)
            {
                return 0;
            }
        }
        catch (IOException) { return 0; }

        string text;
        try
        {
            text = File.ReadAllText(full);
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }

        var take = Math.Min(text.Length, Math.Max(0, budget - rel.Length - 16));
        if (take <= 0)
        {
            return 0;
        }

        sb.Append("--- ").Append(rel).Append(" ---\n");
        sb.Append(text.AsSpan(0, take));
        if (take < text.Length)
        {
            sb.Append("\n[…truncated]");
        }

        sb.Append("\n\n");
        return take + rel.Length + 16;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static string Relative(string workdir, string full) =>
        Path.GetRelativePath(workdir, full).Replace(Path.DirectorySeparatorChar, '/');

    private static string? FirstLine(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var t = line.Trim().TrimStart('#').Trim();
            if (t.Length > 0)
            {
                return t.Length > 160 ? t[..160] : t;
            }
        }

        return null;
    }
}
