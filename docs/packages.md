# Pacotes e Dependências

## NuGet (ver `*.csproj` para versões exatas)

| Projeto | Pacotes principais |
|---|---|
| `OpenWebUI.Api` | `Microsoft.AspNetCore.Authentication.JwtBearer`, hosting WASM |
| `OpenWebUI.Infrastructure` | `Microsoft.EntityFrameworkCore.Sqlite` |
| `OpenWebUI.Client` | `Microsoft.AspNetCore.Components.WebAssembly*` |
| `OpenWebUI.Api.Tests` | `NUnit`, `NUnit3TestAdapter`, `Microsoft.NET.Test.Sdk` |

## Frontend

| Item | Origem |
|---|---|
| Tailwind CSS v4 | CLI standalone (`~/.local/bin/tailwindcss`, via blueprint) |
| `@tailwindcss/typography` | plugin resolvido pelo CLI (prose das mensagens) |
| Inter Variable | asset local `wwwroot/assets/fonts/Inter-Variable.ttf` |

Sem `node_modules` nem build Node — o CSS é gerado pelo CLI standalone e commitado.

## Ferramentas de ambiente

- `dotnet` em `/home/ubuntu/.dotnet/dotnet` (instalado via blueprint).
- `tailwindcss` CLI standalone (instalado via blueprint).
