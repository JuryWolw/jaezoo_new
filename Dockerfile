# syntax=docker/dockerfile:1.6

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    NUGET_XMLDOC_MODE=skip \
    DOTNET_NOLOGO=1

# 1) Копируем только то, что влияет на restore (чтобы кеш работал)
COPY *.sln ./
# Если есть - раскомментируй:
# COPY NuGet.config ./
# COPY global.json ./
# COPY Directory.Build.props ./
# COPY Directory.Build.targets ./

COPY JaeZoo.Server/*.csproj JaeZoo.Server/

# Restore с кешем NuGet (ускоряет повторные сборки)
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore JaeZoo.Server/JaeZoo.Server.csproj

# 2) Копируем остальной код и publish
COPY . .

RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish JaeZoo.Server/JaeZoo.Server.csproj \
      -c Release -o /app/publish --no-restore \
      /p:UseAppHost=false

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ARG APP_UID=10001
RUN set -eux; \
    if ! getent group app >/dev/null; then groupadd -g ${APP_UID} app; fi; \
    if ! id -u app >/dev/null 2>&1; then useradd -u ${APP_UID} -g app -m -s /usr/sbin/nologin app; fi

COPY --from=build --chown=app:app /app/publish ./

USER app

# Render задаёт PORT. Делаем так, чтобы Kestrel слушал именно его.
# И даём дефолт на локалку.
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080}

# Можно оставить 8080 — EXPOSE декларативный, Render всё равно проксирует на PORT.
EXPOSE 8080

ENTRYPOINT ["dotnet", "JaeZoo.Server.dll"]
