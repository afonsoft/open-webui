---
paths:
  - "**/*.cs"
  - "**/*.csproj"
  - "**/*.slnx"
---

# Regras .NET

- Camadas: `Api → Infrastructure → Application → Domain`; `Client` só referencia `Application`. Não criar dependência reversa.
- Endpoints Minimal API em `src/OpenWebUI.Api/Endpoints/` — registrar no `Program.cs`.
- Erros de provider LLM → status de erro (`NotFound`/`BadGateway`); nunca retorno enlatado que persista sobre dados do usuário.
- Mudança de entidade → atualizar `SchemaBootstrap` (evolução automática, sem migrações EF).
- Chaves de API no formato `sk-*`; middleware aceita `Bearer sk-*` além de JWT.
- Novo teste → `tests/OpenWebUI.Api.Tests` (NUnit, nomes em pt-BR).
- Warnings como erros em Release; `dotnet build` deve sair limpo.
