# AD-0002 — SQLite + EF Core Migrations com baseline de bases legadas

## Context

O upstream usa SQLite via Alembic com `ensure_created` automático. A migração
precisava evoluir schema sem migrações manuais e sem perder dados de `webui.db`
já criadas fora do histórico de migrações.

## Decision

- SQLite em `data/openwebui.db` (fora do `wwwroot` — nunca servido estaticamente;
  `data/`, `*.db*` no `.gitignore`/`.dockerignore`, `VOLUME /app/data` no Docker).
- EF Core Migrations como única forma de evolução de schema.
- `DatabaseMigrator` no boot: roda `Migrate()` e faz baseline — se o banco já
  tem as tabelas mas não tem `__EFMigrationsHistory`, marca as migrações como
  aplicadas sem recriar nada.
- Chave composta `(ChatId, Id)` em `ChatMessages` (mesma semântica do upstream).

## Consequences

- Positive: upgrades automáticos e idempotentes; zero perda em bases legadas;
  multi-DB possível trocando `ConnectionStrings__Default`.
- Trade-off: single-writer SQLite — não escala multi-instância (upstream tem o
  mesmo limite); Postgres não suportado hoje.
