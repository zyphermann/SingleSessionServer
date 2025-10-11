# SingleSessionServer

Eine Minimal-API, die genau eine aktive Session pro Spieler zulässt.

## Voraussetzungen
- .NET 8 SDK (getestet mit .NET 8.0)
- Optional: konfigurierte SMTP-Zugangsdaten in `appsettings.json`, wenn echte E-Mails versendet werden sollen.

## Server starten
1. Abhängigkeiten wiederherstellen (einmalig): `dotnet restore`
2. Server lokal starten: `dotnet run`
3. Standardmäßig lauscht die Anwendung auf `https://localhost:7082` und `http://localhost:5082`. Die genauen Ports werden beim Start im Terminal ausgegeben.

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
