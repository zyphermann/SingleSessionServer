# SingleSessionServer

A minimal API that allows exactly one active session per player.

## Prerequisites
- .NET 8 SDK (tested with .NET 8.0)
- PostgreSQL 16 (reachable locally or via Docker Compose from this project)
- Optional: configured SMTP credentials in `appsettings.json` if real emails should be sent.

## Start the server
1. Restore dependencies (once): `dotnet restore`
2. Ensure the database is running (for example `docker compose up db`)
3. Start the server locally: `dotnet run`
4. By default the app listens on `https://localhost:7082` and `http://localhost:5082`. The exact ports are printed on startup.

## Docker
1. Build the container image: `docker build -t singlesessionserver .`
2. Start the container:  
   ```bash
   docker run --rm -p 8880:8880 --name singlesessionserver singlesessionserver
   ```
   The application will then be available at `http://localhost:8880`.

## Docker Compose
1. Review/adjust `.env` (`DB_*` variables as well as `ConnectionStrings__Default` for the app container).
2. Start the services: `docker compose up --build`
3. Open the application: `http://localhost:8880`
4. Access the Adminer UI at `http://localhost:8081` (server: `db`, credentials from `.env`).
5. The database is reachable locally at `localhost:${DB_PORT}` (default `55532`).
6. Stop and clean up the containers with `docker compose down`.

## Example flow
1. **Register browser/player**  
   ```bash
   curl -i -X POST http://localhost:5082/device/init
   ```
   The response sets the `player_id` and `device_id` cookies. Include both cookies in follow-up requests.

2. **Create a session**  
   ```bash
   curl -i -X POST http://localhost:5082/session/login \
        --cookie "player_id=<value-from-step-1>" \
        --cookie "device_id=<value-from-step-1>"
   ```
   The response contains a `sess_id` cookie. The server now has exactly one active session for the player.

3. **Check status**  
   ```bash
   curl -i http://localhost:5082/whoami \
        --cookie "player_id=<playerId>" \
        --cookie "device_id=<deviceId>" \
        --cookie "sess_id=<sessId>"
   ```
   The currently authenticated combination of `playerId`, `deviceId`, and `sessId` is returned.

## Additional notes
- `POST /device/transfer/start` can send a one-time magic-link email to transfer a session to another browser.
- `GET /server/info` returns runtime information about the process (uptime, environment, etc.).
- Devices (`devices`) and sessions (`sessions`) are persisted in PostgreSQL. Initial tables are created automatically by `db/init/01-schema.sql`.

## Games
- Game definitions live in the new `games` table (slug, display name, default game state as JSON).
- Individual game states per player are stored in `game_states` (linking `player` ↔ `game`).
- `PUT /api/games/{slug}` lets you create or update games (including the default state).
- The engine can call `POST /api/games/{slug}/state/load`: if no record exists for the player, the default game state is copied and stored automatically. The response includes the state and related metadata.
