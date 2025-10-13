# SingleSessionServer

A minimal API that allows exactly one active session per player.

## Prerequisites

- .NET 8 SDK (tested with .NET 8.0)
- PostgreSQL 16 (reachable locally or via Docker Compose from this project)
- Optional: configured SMTP credentials (e.g. via `.env` using `SMTP__*` variables or in `appsettings.json`) if real emails should be sent.

## Start the server

1. Restore dependencies (once): `dotnet restore`
2. Ensure the database is running (for example `docker compose up db`)
3. Start the server locally: `dotnet run`
4. By default the app listens on `https://localhost:7082` and `http://localhost:5082`. The exact ports are printed on startup.
   The application reads a local `.env` (if present) and composes the PostgreSQL connection string from the `DB_*` entries.
   Set `App__PublicBaseUrl` in your `.env` to the externally reachable URL/port (e.g. `http://localhost:8880`) so verification links in emails point to the right host.

## Docker

1. Build the container image: `docker build -t singlesessionserver .`
2. Start the container:
   ```bash
   docker run --rm -p 8880:8880 --name singlesessionserver singlesessionserver
   ```
   The application will then be available at `http://localhost:8880`.

## Docker Compose

1. Review/adjust `.env` (`DB_*` variables, `App__PublicBaseUrl`, and optional `SMTP__*` settings). The application derives its connection string from these values at runtime.
2. If you want to use the diagnostics email test endpoint, also set `Diagnostics__EmailTest__Recipient` in `.env`.
3. Start the services: `docker compose up --build`
4. Open the application: `http://localhost:8880`
5. Access the Adminer UI at `http://localhost:8081` (server: `db`, credentials from `.env`).
6. The database is reachable locally at `localhost:${DB_PORT}` (default `55532`).
7. Stop and clean up the containers with `docker compose down`.

## Example flow

1. **Register browser/player**

   ```bash
   curl -i -X POST http://localhost:5082/device/init
   ```

   The response sets the `player_id` and `device_id` cookies and returns the short player code (`playerShortId`). Include both cookies in follow-up requests.

2. **Create a session**

   ```bash
   curl -i -X POST http://localhost:5082/session/login \
        --cookie "player_id=<value-from-step-1>" \
        --cookie "device_id=<value-from-step-1>"
   ```

   The response contains a `session_id` cookie. The server now has exactly one active session for the player.

3. **Check status**
   ```bash
   curl -i http://localhost:5082/whoami \
        --cookie "player_id=<playerId>" \
        --cookie "device_id=<deviceId>" \
        --cookie "session_id=<sessId>"
   ```
   The currently authenticated combination of `playerId`, `deviceId`, and `sessId` is returned.

## Additional notes

- `POST /device/transfer/start` can send a one-time magic-link email to transfer a session to another browser.
- `GET /server/info` returns runtime information about the process (uptime, environment, etc.).
- `GET /whoami/{sessionId}` returns player information for a session id (helpful for Godot/mobile clients storing only the session token).
- `POST /session/login/short` lets a device join an existing player account by providing the short id (after calling `/device/init`).
- `POST /session/login/direct` performs the login without relying on cookies (pass `playerId` or `playerShortId` and optionally a known `deviceId`).
- Devices (`devices`) and sessions (`sessions`) are persisted in PostgreSQL. Initial tables are created automatically by `db/init/01-schema.sql`.
- Authenticated players can initiate email verification via `POST /email/verification/start`; the link in the email targets `GET /email/verification/confirm?token=…`. Only confirmed addresses are persisted on player records.

## Games

- Game definitions live in the new `games` table (slug, display name, default game state as JSON).
- Individual game states per player are stored in `game_states` (linking `player` ↔ `game`).
- `PUT /api/games/{slug}` lets you create or update games (including the default state).
- The engine can call `POST /api/games/{slug}/state/load`: if no record exists for the player, the default game state is copied and stored automatically. The response includes the state and related metadata.
- Optional: configured SMTP credentials (e.g. via `.env` using `SMTP__*` variables or in `appsettings.json`) if real emails should be sent. For testing, you can set `Diagnostics__EmailTest__Recipient` to your address and use the `/diagnostics/email-test` endpoint once SMTP is configured.
