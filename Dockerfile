# syntax=docker/dockerfile:1
# Open WebUI (.NET) — multi-stage build, padrão afonsoft/KnowledgeRAG.
# Stage 1: .NET SDK 10 → publish self-contained para linux-x64/arm64.
# Stage 2: runtime-deps (Debian slim), usuário non-root, volume /data, healthcheck.
#
# NOTA: linux-musl-x64/Alpine não é possível — o cliente Blazor WASM hospedado
# restaura Microsoft.NETCore.App.Runtime.Mono.<rid>, que não é publicado para
# musl. bookworm-slim é a menor base glibc que mantém shell para o HEALTHCHECK.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore primeiro (cache de camadas) — só os projetos que a Api referencia.
COPY OpenWebUI.slnx ./
COPY src/OpenWebUI.Domain/OpenWebUI.Domain.csproj             src/OpenWebUI.Domain/
COPY src/OpenWebUI.Application/OpenWebUI.Application.csproj   src/OpenWebUI.Application/
COPY src/OpenWebUI.Infrastructure/OpenWebUI.Infrastructure.csproj src/OpenWebUI.Infrastructure/
COPY src/OpenWebUI.Api/OpenWebUI.Api.csproj                   src/OpenWebUI.Api/
COPY src/OpenWebUI.Client/OpenWebUI.Client.csproj             src/OpenWebUI.Client/
# NOTA: sem -r aqui — um RID no `dotnet restore` se propaga ao client Blazor
# WASM, que passa a exigir um pacote Mono.<rid> inexistente. O RID fica no
# `publish`, cuja restore implícita o delimita corretamente.
# O cache mount do BuildKit preserva o cache NuGet entre builds — a CI
# publica cache type=gha, então os restores dentro da imagem não baixam de novo.
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore src/OpenWebUI.Api/OpenWebUI.Api.csproj

# wasm-tools: sem ele o publish do client Blazor pula os passes de otimização
# (o SDK avisa "Publishing without optimizations"). O passo nativo roda emcc,
# que chama `python` — ausente na imagem sdk, então instala python3 e faz alias.
RUN dotnet workload install wasm-tools \
 && apt-get update \
 && apt-get install -y --no-install-recommends python3 \
 && ln -sf /usr/bin/python3 /usr/local/bin/python \
 && rm -rf /var/lib/apt/lists/*

COPY src/ src/
# O RID segue a arquitetura da própria imagem (nativa ou --platform/QEMU):
# `docker build` simples não injeta TARGETARCH, então uname é a fonte confiável.
RUN --mount=type=cache,target=/root/.nuget/packages \
    case "$(uname -m)" in \
      x86_64)  RID=linux-x64 ;; \
      aarch64) RID=linux-arm64 ;; \
      *) echo "Unsupported arch: $(uname -m)" >&2; exit 1 ;; \
    esac && \
    dotnet publish src/OpenWebUI.Api/OpenWebUI.Api.csproj \
      -c Release -r "$RID" --self-contained true -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime

# curl para o HEALTHCHECK; a imagem base já traz o usuário non-root `app` (uid 1654).
RUN apt-get update \
 && apt-get upgrade -y \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /data \
 && chown app:app /data

WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    WEBUI_DATA_DIR=/data \
    ConnectionStrings__Default="Data Source=/data/openwebui.db"

EXPOSE 8080
VOLUME ["/data"]
USER app

# Probe /health — retorna 200 quando o processo está de pé.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD curl -fsS http://localhost:8080/health -o /dev/null || exit 1

ENTRYPOINT ["./OpenWebUI.Api"]
