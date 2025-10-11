# SingleSessionServer

Eine Minimal-API, die genau eine aktive Session pro Spieler zulässt.

## Voraussetzungen
- .NET 8 SDK (getestet mit .NET 8.0)
- PostgreSQL 16 (lokal erreichbar oder via Docker Compose aus diesem Projekt)
- Optional: konfigurierte SMTP-Zugangsdaten in `appsettings.json`, wenn echte E-Mails versendet werden sollen.

## Server starten
1. Abhängigkeiten wiederherstellen (einmalig): `dotnet restore`
2. Sicherstellen, dass die Datenbank läuft (z.B. `docker compose up db`)
3. Server lokal starten: `dotnet run`
4. Standardmäßig lauscht die Anwendung auf `https://localhost:7082` und `http://localhost:5082`. Die genauen Ports werden beim Start im Terminal ausgegeben.

## Docker
1. Container-Image bauen: `docker build -t singlesessionserver .`
2. Container starten:  
   ```bash
   docker run --rm -p 8880:8880 --name singlesessionserver singlesessionserver
   ```
   Die Anwendung ist dann unter `http://localhost:8880` erreichbar.

## Docker Compose
1. `.env` prüfen/anpassen (`DB_*` Variablen sowie `ConnectionStrings__Default` für den App-Container).
2. Services starten: `docker compose up --build`
3. Anwendung aufrufen: `http://localhost:8880`
4. Adminer UI erreicht ihr unter `http://localhost:8081` (Server: `db`, User/Pass laut `.env`).
5. Datenbank erreicht ihr lokal unter `localhost:${DB_PORT}` (Standard `55532`).
6. Mit `docker compose down` die Container wieder stoppen und aufräumen.

## Beispielablauf
1. **Browser/Player registrieren**  
   ```bash
   curl -i -X POST http://localhost:5082/device/init
   ```
   Antwort enthält ein `playerId`-Cookie. Diesen Cookie für die weiteren Schritte verwenden.

2. **Session erstellen**  
   ```bash
   curl -i -X POST http://localhost:5082/session/login --cookie "player_id=<Wert-aus-Schritt-1>"
   ```
   Antwort enthält ein `sess_id`-Cookie. Der Server hat jetzt genau eine aktive Session für den Spieler.

3. **Status prüfen**  
   ```bash
   curl -i http://localhost:5082/whoami --cookie "player_id=<Wert>" --cookie "sess_id=<Wert>"
   ```
   Es wird die aktuell angemeldete Kombination aus `playerId` und `sessId` zurückgegeben.

## Weitere Hinweise
- Über `POST /device/transfer/start` kann eine einmalige Magic-Link-Mail erzeugt werden, um eine Session auf einen anderen Browser zu übertragen.
- `GET /server/info` zeigt Laufzeit-Informationen zum Prozess (z.B. Uptime, Environment).
- Geräte (`devices`) und Sessions (`sessions`) werden in PostgreSQL persistiert. Initiale Tabellen legt `db/init/01-schema.sql` automatisch an.
