# Open WebUI — .NET

Migração do [Open WebUI](https://github.com/open-webui/open-webui) para
**.NET 10 · C# 14 · Blazor WebAssembly** (SvelteKit + FastAPI → ASP.NET Core + Blazor).

## Estrutura

```
dotnet/
├── OpenWebUI.slnx
├── src/
│   ├── OpenWebUI.Shared/   # Contratos/DTOs compartilhados
│   ├── OpenWebUI.Server/   # API ASP.NET Core (serve o cliente)
│   └── OpenWebUI.Client/   # App Blazor WebAssembly
└── tests/
    └── OpenWebUI.Server.Tests/  # Testes NUnit
```

## Executar

Requisito: SDK do .NET 10.

```bash
cd dotnet
dotnet restore OpenWebUI.slnx
dotnet run --project src/OpenWebUI.Server
# http://localhost:8080 — o primeiro usuário cadastrado vira admin
```

## Testar

```bash
cd dotnet
dotnet test OpenWebUI.slnx
```

## Documentação

- [docs/MIGRACAO-DOTNET.md](docs/MIGRACAO-DOTNET.md) — mapa de paridade com o
  upstream (o que está migrado, parcial e pendente) e roadmap.
- [dotnet/README.md](dotnet/README.md) — detalhes de arquitetura e configuração.

## Licença

Este fork mantém os arquivos de licença do projeto original
(`LICENSE`, `LICENSE_HISTORY`, `LICENSE_NOTICE`).
