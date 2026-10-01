# Tecnologias

| Camada | Tecnologia | Versão |
|--------|-----------|--------|
| Runtime | .NET | 10.0 (SDK 10.0.401) |
| Linguagem | C# | 14 |
| Frontend | Blazor WebAssembly | .NET 10 |
| Backend | ASP.NET Core Minimal APIs | .NET 10 |
| ORM | Entity Framework Core | 10.0 |
| Banco | SQLite (`webui.db`) | — |
| Auth | JWT Bearer + chaves `sk-*` | — |
| CSS | Tailwind CSS | v4 (saída estática) |
| Fonte | Inter Variable | `wwwroot/assets/fonts/` |
| Testes | NUnit + NUnit3TestAdapter | — |
| Container | Docker multi-stage | `mcr.microsoft.com/dotnet/*:10.0` |
| CI | GitHub Actions | `ubuntu-latest` |

## Notas

- `MapStaticAssets()` é obrigatório para servir o WASM (placeholders `#[.{fingerprint}]`).
- CSS Tailwind é gerado fora do build .NET (`tailwind.input.css` → `wwwroot/css/tailwind.css`, commitado).
- Tema: `.dark` no `<html>` + `localStorage webui.theme`, aplicado pre-paint.
