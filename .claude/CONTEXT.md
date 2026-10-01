# CONTEXT.md — Engenharia de Contexto

## Estratégias de carregamento

| Tipo | Quando | Exemplos |
|---|---|---|
| Always-on | Toda sessão | `CLAUDE.md`, `.claude/rules/global-rules.md` |
| Pattern-matched | Por tipo de arquivo | `rules/dotnet.md` (`paths: ['**/*.cs']`), `rules/blazor.md` (`paths: ['**/*.razor']`) |
| On-demand | Quando referenciado | `.claude/knowledge/`, `docs/`, `docs/MIGRACAO-DOTNET.md` |
| Progressive disclosure | Arquivos grandes | mapa de dirs → headers → seções necessárias |

## Hierarquia de prioridade

1. `CLAUDE.md` e `global-rules.md` — não-negociáveis.
2. `.claude/memory/orchestrator_stats.md` — estado operacional.
3. Regras que casam com arquivos sendo modificados.
4. Skills relevantes ao pedido.
5. `docs/` e `.claude/knowledge/` explicitamente referenciados.
6. `.claude/MEMORY.md` — hint, não verdade; código vence.

## Orçamento de tokens

- Reservar **20% da janela para output**.
- `CLAUDE.md` ≤ 500 linhas.
- Arquivos > 500 linhas: ler cabeçalho/índice primeiro, depois só as seções necessárias.
- CSS gerado (`wwwroot/css/tailwind.css`, ~200KB): nunca carregar inteiro — editar `tailwind.input.css` e regenerar.

## Compaction ladder

1. Budget reduction — descartar on-demand irrelevante.
2. Snip — cortar saídas verbosas para linhas de resumo.
3. Microcompact — resumir sub-tarefas concluídas em uma linha.
4. Collapse — substituir grupo finalizado por checkpoint em `orchestrator_stats.md`.
5. Auto-compact — último recurso; escrever memória antes.
