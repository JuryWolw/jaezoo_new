# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    NUGET_XMLDOC_MODE=skip \
    DOTNET_NOLOGO=1

#  опируем весь репозиторий сразу (надЄжно дл€ любой структуры)
COPY . .

# ≈сли есть .sln Ч используем его, иначе восстанавливаем и публикуем первый найденный csproj
RUN if ls *.sln >/dev/null 2>&1; then \
      dotnet restore; \
    else \
      dotnet restore $(find . -maxdepth 3 -name "*.csproj" | head -n 1); \
    fi

# ѕубликуем сервер:
# 1) если есть .sln Ч пытаемс€ опубликовать проект, который содержит "Server"
# 2) иначе публикуем первый найденный csproj
RUN if ls *.sln >/dev/null 2>&1; then \
      dotnet publish $(find . -maxdepth 4 -name "*.csproj" | grep -i "server" | head -n 1) \
        -c Release -o /app/publish --no-restore /p:UseAppHost=false; \
    else \
      dotnet publish $(find . -maxdepth 4 -name "*.csproj" | head -n 1) \
        -c Release -o /app/publish --no-restore /p:UseAppHost=false; \
    fi

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ARG APP_UID=10001
RUN set -eux; \
    if ! getent group app >/dev/null; then groupadd -g ${APP_UID} app; fi; \
    if ! id -u app >/dev/null 2>&1; then useradd -u ${APP_UID} -g app -m -s /usr/sbin/nologin app; fi

COPY --from=build --chown=app:app /app/publish ./

USER app

# Render задаЄт PORT Ч слушаем его. Ћокально будет 8080.
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080}
EXPOSE 8080

ENTRYPOINT ["dotnet", "JaeZoo.Server.dll"]
