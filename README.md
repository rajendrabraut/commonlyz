# Commonlyz

One self-hosted .NET 10 platform that replaces 15 everyday SaaS tools — one build, one database, one sign-in, one admin dashboard.

Frontend **and** backend are .NET only: **Blazor Web App** (interactive server rendering, no JavaScript framework) on **ASP.NET Core 10**, **EF Core 10** and **SQL Server 2022**.

| Group | App | Instead of | Admin page | Public page |
|---|---|---|---|---|
| Personal tools | **ShortLink** — short links with click stats | Bitly | `/shortlink` | `/s/{slug}` |
| | **LinkHub** — link-in-bio pages | Linktree | `/linkhub` | `/p/{handle}` |
| | **PixelSqueeze** — compress/resize/convert images | TinyPNG | `/pixelsqueeze` | — |
| | **Journal** — private Markdown journal | Day One | `/journal` | — |
| | **SplitShare** — shared expenses | Splitwise | `/splitshare` | — |
| | **Budget** — envelope budgeting | YNAB | `/budget` | — |
| Workflows | **TimeTrack** — timers, weekly view, CSV | Toggl Track | `/timetrack` | — |
| | **FormFlow** — one-question-at-a-time forms | Typeform | `/formflow` | `/f/{slug}` |
| | **SlotBook** — booking pages | Calendly | `/slotbook` | `/book/{slug}` |
| | **StockRoom** — inventory ledger | Sortly | `/stockroom` | — |
| | **ClientCollect** — client request portal | Content Snare | `/clientcollect` | `/portal/{token}` |
| Team | **TaskBoard** — kanban with drag & drop | Trello | `/taskboard` | — |
| | **Wiki** — nested pages with history | Notion | `/wiki` | — |
| | **Chat** — channels, threads, real time | Slack | `/chat` | — |
| | **StatusBoard** — status page & incidents | Statuspage | `/statusboard` | `/status`, `/status.json` |

## Run it

### Docker (everything)
```bash
cp .env.example .env          # set ADMIN_PASSWORD and MSSQL_SA_PASSWORD
docker compose up --build
```
Open http://localhost:8080 and sign in with `ADMIN_EMAIL` / `ADMIN_PASSWORD`. The first admin is created only when the database has no users.

### Local development
```bash
docker compose up -d sql                       # SQL Server 2022 on :1433
dotnet run --project src/Commonlyz.Web         # http://localhost:5100
```
To see real emails locally, run a catch-all SMTP server such as [Mailpit](https://mailpit.axllent.org) (`docker run -p 1025:1025 -p 8025:8025 axllent/mailpit`) and start the app with `Email__Host=localhost Email__Port=1025 Email__Security=None`.
Development sign-in: `admin@commonlyz.local` / `Admin123!` (from `appsettings.Development.json`). Every module's migrations run on startup.

### Tests
```bash
dotnet test     # 130 tests; integration tests start SQL Server via Testcontainers (Docker required)
```

## Architecture

```
src/
  Commonlyz.Core/        Module contract (IAppModule), DB helpers, shared Blazor components,
                         money/time-zone/CSV/Markdown helpers. No module depends on another.
  Commonlyz.Web/         The single host: Identity (users, roles), login, dashboard, user admin,
                         layout, module registration (ModuleCatalog.cs), migrations at startup.
  Modules/
    Commonlyz.Modules.<App>/   One Razor class library per app:
      <App>Module.cs           registration, endpoints, dashboard stats
      Data/                    DbContext in its own SQL schema + EF migrations
      <App>Service.cs          business rules (all writes go through here)
      Pages/                   admin pages ([Authorize])
      Public/                  anonymous pages (PublicLayout)
tests/Commonlyz.Tests/   Unit tests + Testcontainers integration tests for every module
eng/                     new-module.sh, register-module.py, add-migration.sh, dev-restart.sh
```

- **Separate projects, one build.** Each app is its own project and owns its own schema (`shortlink.*`, `timetrack.*`, …) with its own migrations history, inside one shared database. The host references every module and lists them in `ModuleCatalog.cs`.
- **Blazor Server everywhere**, except login and the static public pages (LinkHub profile, status page), which use static server rendering. Pages use `IDbContextFactory` with short-lived contexts, the right pattern for long-lived circuits.
- **Real-time chat** uses the Blazor circuit's existing connection through an in-process notifier. No extra hub, no JavaScript.

### Rules each app gets right (and tests)
| App | Rule |
|---|---|
| ShortLink | Slug uniqueness enforced by a unique index (10 concurrent creates → 1 wins); 302 redirects; redirect-loop and `javascript:` URLs rejected; clicks batched off the hot path |
| TimeTrack | One running timer per user (filtered unique index + `UPDLOCK`); weekly totals split at local midnight with NodaTime (DST-correct) |
| SlotBook | Overlap check + insert in one serializable transaction (10 concurrent bookings → 1 wins); only offered slots can be booked |
| StockRoom | Append-only ledger; stock can't go negative under concurrency; transfers are paired rows |
| SplitShare | Integer cents; shares always sum to the amount (largest-remainder rounding); balances sum to zero |
| Budget | Σ category available + Ready to assign = Σ account balances; transfers don't touch the budget |
| FormFlow | Publishing snapshots an immutable version; answers validated server-side; honeypot |
| ClientCollect | Magic-link tokens stored hashed; uploads served as attachments only; reminders stop when everything is accepted |
| Wiki | Optimistic concurrency (stale saves rejected); no page can be moved under its own descendant; full revision history |
| Chat | Private channels enforced in one query used by channel list, messages, threads and search |
| LinkHub / StatusBoard | Unpublished profiles and draft links never public; resolving an incident restores components only if no other incident affects them |

## Configuration

| Setting | Purpose |
|---|---|
| `ConnectionStrings:Default` | SQL Server connection string |
| `Admin:Email`, `Admin:Password`, `Admin:DisplayName` | First admin, created on an empty database only |
| `Database:MigrateOnStartup` | Apply all module migrations at startup (default `true`) |
| `Email:Host`, `Email:Port`, `Email:Security` | SMTP server. Leave `Host` empty to write emails to the log instead. `Security`: `Auto` (default), `StartTls`, `SslOnConnect`, `None` |
| `Email:Username`, `Email:Password` | SMTP login (omit for servers that don't require auth) |
| `Email:FromAddress`, `Email:FromName` | Sender shown to recipients |
| `Email:DispatcherEnabled` | Run the delivery worker in this instance (default `true`; turn off on web-only nodes) |
| `Email:MaxAttempts`, `Email:PollInterval`, `Email:BatchSize` | Retry and polling tuning (defaults 8, 10 s, 20) |

Environment variables use `__`, e.g. `ConnectionStrings__Default`, `Admin__Password`.

## Adding an app
```bash
./eng/new-module.sh Invoices                 # scaffold src/Modules/Commonlyz.Modules.Invoices
# write InvoicesModule : IAppModule, Data/InvoicesDb.cs, a service and pages
python3 eng/register-module.py Invoices      # project reference + ModuleCatalog + solution
./eng/add-migration.sh Invoices              # EF migration in the module's own schema
```

## Production notes
- Run behind a reverse proxy with HTTPS; set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`.
- Data Protection keys are stored in the database, so sign-in cookies survive restarts and work across instances.
- Chat's real-time notifier and PixelSqueeze's temporary file cache are in-memory: run a single instance, or add a backplane (Redis / Azure SignalR) before scaling out.
- Email (SlotBook confirmations with cancel links, ClientCollect reminders) uses a **database outbox**: `INotificationSender` only queues, and a background dispatcher sends over SMTP (MailKit), with exponential backoff up to `MaxAttempts`. A slow or down mail server never fails a booking or page, and nothing is lost on restart. Rows are claimed with a lease (`UPDATE … OUTPUT` with `READPAST`), so several instances can run the dispatcher without double-sending. Admins can see delivery status, errors and retries, and send a test email, at **/email**.
- Back up the database (Azure SQL point-in-time restore, or `BACKUP DATABASE … TO URL`). ClientCollect uploads (10 MB each) live in it too.
