# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 1) Сначала только файлы проекта для кешируемого restore
COPY *.csproj ./
RUN dotnet restore

# 2) Потом остальной код
COPY . ./

# 3) Publish без restore
RUN dotnet publish -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ARG APP_UID=10001
RUN set -eux; \
    if ! getent group app >/dev/null; then groupadd -g ${APP_UID} app; fi; \
    if ! id -u app >/dev/null 2>&1; then useradd -u ${APP_UID} -g app -m -s /usr/sbin/nologin app; fi

# Копируем сразу с владельцем — быстрее и можно убрать отдельный chown
COPY --from=build --chown=app:app /app/publish ./

USER app

ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT}
EXPOSE 8080
ENTRYPOINT ["dotnet", "JaeZoo.Server.dll"]
