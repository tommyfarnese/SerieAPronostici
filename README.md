# Serie A Pronostici

Prototipo locale per analizzare le prossime partite di Serie A. Il frontend React legge i dati dall'API ASP.NET Core. Il backend importa calendario e risultati da Football-Data.org e li salva in Azure SQL. Senza credenziali configurate usa dati dimostrativi.

## Requisiti
- Node.js 20+
- .NET 10 SDK

## Avvio locale
Avvia il backend:

```powershell
cd backend/SerieAPronostici.Api
dotnet run --urls http://localhost:5080
```

In un secondo terminale avvia il frontend:

```powershell
cd frontend
npm install
npm run dev
```

Il frontend inoltra `/api` al backend locale sulla porta 5080. L'API espone `GET /api/health` e `GET /api/matches`.

## Football-Data.org e Azure SQL

1. Registra un client su [football-data.org](https://www.football-data.org/client/register) e copia il token API.
2. Configura `ConnectionStrings__AzureSql` come variabile d'ambiente del backend. Non inserirla nel repository.
3. Salva il token nei secrets .NET del progetto (non condividerlo in chat):

```powershell
cd backend/SerieAPronostici.Api
dotnet user-secrets init
dotnet user-secrets set "FootballData:ApiToken" "INCOLLA_IL_TOKEN_NEL_TERMINALE"
```

Riavvia il backend in ambiente Development per caricare i secrets (`$env:ASPNETCORE_ENVIRONMENT="Development"`). Football-Data.org importa la competizione Serie A (`SA`), usa la stagione attiva e aggiorna i dati ogni 24 ore. Il token resta solo nel backend.

Al primo avvio con Azure SQL configurato, l'API crea le tabelle. Se manca il token inserisce dati demo; con token e database configurati importa i dati reali. Per il primo avvio usa un database dedicato e vuoto.

Il piano gratuito include la Serie A e limita le richieste a 10 al minuto. Calendario e risultati possono essere ritardati e non sono adatti a seguire il live. Il sito mostra l'attribuzione richiesta da Football-Data.org nel piè di pagina.

## Pubblicazione
- Conserva token e connection string nelle impostazioni sicure dell'hosting, mai nel repository.
- Per un frontend pubblicato su GitHub Pages, imposta `VITE_API_BASE_URL` con l'URL HTTPS dell'API Azure durante la build e autorizza l'origine GitHub Pages in `FrontendOrigin`.
