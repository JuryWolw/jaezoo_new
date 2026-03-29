# JaeZoo.Server — call signaling integration notes

## Что добавлено
- `Hubs/CallsHub.cs` — отдельный SignalR hub для звонков.
- `Controllers/CallsController.cs` — REST API для получения ICE config, списка активных звонков и старта звонка.
- `Models/Calls/CallContracts.cs` — DTO/enum/сессия звонка.
- `Services/Calls/TurnOptions.cs` — настройки TURN.
- `Services/Calls/TurnCredentialsService.cs` — генерация временных TURN credentials по shared secret.
- `Services/Calls/CallSessionService.cs` — in-memory active call store.
- `Services/Calls/CallAuditService.cs` — структурированное логгирование событий звонка.
- `Services/Storage/LocalObjectStorage.cs` — fallback, чтобы сервер не падал без S3-настроек.

## Что изменено
- `Program.cs`
  - добавлены регистрации call services;
  - JWT для `/hubs/calls`;
  - map нового хаба `/hubs/calls`;
  - fallback на local object storage, если S3 не настроен;
  - чтение секции `Turn`.
- `appsettings.json`
  - добавлена секция `Turn`.

## Новые события SignalR
- `call.invite`
- `call.state`
- `call.accepted`
- `call.declined`
- `call.busy`
- `call.offer`
- `call.answer`
- `call.ice-candidate`
- `call.connected`
- `call.failed`
- `call.ended`

## Новый REST API
- `GET /api/calls/ice-config`
- `GET /api/calls/active`
- `POST /api/calls/start`

## Важные настройки
В `appsettings.json` надо заменить:
- `Turn:Secret`
- при необходимости `Turn:Urls`
- S3-настройки object storage, если нужен production upload path

## Ограничения текущей серверной версии
- история звонков пока не пишется в БД;
- групповые звонки пока не реализованы;
- таймауты/cleanup активных звонков пока не вынесены в background service;
- client-specific ack/sequence пока нет.
