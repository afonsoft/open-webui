# syntax=docker/dockerfile:1

# ---------- Build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copia os csproj primeiro para aproveitar cache de restore.
COPY OpenWebUI.slnx .
COPY src/OpenWebUI.Domain/OpenWebUI.Domain.csproj src/OpenWebUI.Domain/
COPY src/OpenWebUI.Application/OpenWebUI.Application.csproj src/OpenWebUI.Application/
COPY src/OpenWebUI.Infrastructure/OpenWebUI.Infrastructure.csproj src/OpenWebUI.Infrastructure/
COPY src/OpenWebUI.Api/OpenWebUI.Api.csproj src/OpenWebUI.Api/
COPY src/OpenWebUI.Client/OpenWebUI.Client.csproj src/OpenWebUI.Client/
RUN dotnet restore src/OpenWebUI.Api/OpenWebUI.Api.csproj

COPY src/ src/
RUN dotnet publish src/OpenWebUI.Api/OpenWebUI.Api.csproj -c Release -o /app/publish --no-restore

# ---------- Runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Dados (SQLite + uploads) — monte um volume para persistir.
ENV ConnectionStrings__Default="Data Source=/app/data/openwebui.db"
VOLUME ["/app/data"]

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "OpenWebUI.Api.dll"]
