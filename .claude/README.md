# .claude/ — Harness de Agentes

Infraestrutura de contexto para Claude Code e Devin CLI (que lê via `.devin/config.json` → `read_config_from.claude`).

## Estrutura

```
.claude/
├── settings.json          # permissões (deny) + hooks
├── hooks/                 # block-protected-push.sh
├── rules/
│   ├── global-rules.md    # always-on
│   ├── dotnet.md          # paths: **/*.cs, *.csproj, *.slnx
│   └── blazor.md          # paths: **/*.razor, tailwind.input.css, wwwroot/
├── agents/                # sub-agentes: review, plan, test
├── skills/                # skills específicas do repo (ex.: tailwind-css)
├── knowledge/             # conhecimento on-demand (ex.: e2e-testing)
├── memory/                # orchestrator_stats.md + sessions
├── CONTEXT.md  RULES.md  MEMORY.md  TOOLS.md  WORKFLOWS.md  README.md
```

## Como skills/rules carregam

- `rules/` sem `paths:` → sempre ativas; com `paths:` → ativam pelo tipo de arquivo tocado.
- `skills/{nome}/SKILL.md` → invocada por relevância (descrição tripartite: What / Use when / Do NOT use).
- `agents/{nome}.md` → sub-agentes com escopo e ferramentas restritas.

## Adicionar uma skill

1. `mkdir -p .claude/skills/<nome>` e criar `SKILL.md` com frontmatter (`name` == pasta).
2. Descrição tripartite no frontmatter.
3. Conteúdo: Contexto / Comportamento / Restrições / Exemplos.

## Loop de verificação local

```bash
dotnet build OpenWebUI.slnx --configuration Release
dotnet test tests/OpenWebUI.Api.Tests
tailwindcss -i src/OpenWebUI.Client/tailwind.input.css -o src/OpenWebUI.Client/wwwroot/css/tailwind.css --minify  # se tocou .razor
```

## Compatibilidade

| Plataforma | Lê |
|---|---|
| Claude Code | `CLAUDE.md` + `.claude/` nativamente |
| Devin CLI | `CLAUDE.md` + `.claude/` via `.devin/config.json` |
| Devin | `.devin/skills/` (catálogo afonsoft/skills) + blueprint |
