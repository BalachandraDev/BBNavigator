# FlowMind operations guide

## Components
| Piece | Runs as | Needs |
|---|---|---|
| `FlowMind.API` (web app + REST API) | IIS site, app pool *No Managed Code* | SQL Server, PostgreSQL+pgvector, storage folder |
| `FlowMind.Worker` (extraction, OCR, analysis, indexing) | Windows Service, **separate from IIS** | same + outbound HTTPS to the AI providers |
| SQL Server | existing company server | database `FlowMind` |
| PostgreSQL + pgvector | company server or container | database `flowmind_vectors`, extension `vector` (≥0.8 recommended) |

No GPU is needed; all AI runs at the providers.

## First deployment
1. Install the **.NET 8 Hosting Bundle** (server) and restart IIS (`iisreset`).
2. `dotnet publish src/FlowMind.API -c Release -o C:\inetpub\flowmind` and `dotnet publish src/FlowMind.Worker -c Release -o C:\flowmind-worker`.
3. Database: `dotnet ef migrations script --idempotent -p src/FlowMind.Infrastructure -s src/FlowMind.API -o deploy.sql` → review → run on SQL Server. The vector table is created automatically on first use; `db/postgres/001_init.sql` is the manual equivalent if the app's DB user cannot create tables/extensions.
4. Storage: create a private folder (e.g. `D:\FlowMindData`), **outside** any web root. Grant the app-pool identity and the worker's service account *Modify*; nobody else.
5. Secrets as **environment variables** (never in files in the repo): see the table below. Set them on the app pool (IIS → Configuration Editor → `system.webServer/aspNetCore/environmentVariables`) and for the service (`sc config` / registry `Environment` multi-string for `FlowMind.Worker`).
6. Create the worker service: `sc create FlowMind.Worker binPath= "C:\flowmind-worker\FlowMind.Worker.exe" start= auto`, set it to run as a dedicated low-privilege account.
7. HTTPS binding on the site. Set `AllowedHosts` to your host name(s) (e.g. `flowmind.company.local`) instead of `*`.
8. Browse to `/health/ready` → all checks `Healthy`. Sign in as platform admin, create the company.

The app **refuses to start** outside Development if it sees a development signing key, a weak/placeholder platform key, LocalDB, the dev DB password, or no storage path.

## Settings (environment-variable names)
| Setting | Meaning |
|---|---|
| `ConnectionStrings__SqlServer`, `ConnectionStrings__Postgres` | databases |
| `Storage__RootPath` | private document folder |
| `Auth__SigningKey` | ≥32 random chars (e.g. `[Convert]::ToBase64String((1..48 \| % {Get-Random -Max 256}))`); changing it signs everyone out |
| `Security__SecretsKey` | 32+ random characters; encrypts company keys so the platform admin can view them. Required outside Development. Keep it with your other secrets: if it changes, stored company keys become unreadable (use Reset key) |
| `Auth__PlatformAdminKey` | ≥24 random chars, or empty to disable platform-admin login |
| `AiProviders__Gemini__ApiKey / DailyRequestLimit / RequestsPerMinute` | document analysis (API + Worker) |
| `AiProviders__Cloudflare__AccountId / ApiToken / DailyNeuronLimit` | embeddings (API + Worker) |
| `AiProviders__Groq__ApiKey / DailyRequestLimit / RequestsPerMinute` | chat (API) |
| `AiProviders__MistralOcr__ApiKey / DailyPageLimit` | OCR of scanned PDFs (Worker) |
| `Security__RequireHttps`, `Security__AiRequestsPerMinute`, `Swagger__Enabled` | hardening switches |
| `Chat__MaxMessageChars` (8000), `Chat__MaxQuestionChars` (400), `Chat__SourceChars` (4500), `Chat__SourceCharsEach` (1000), `Chat__HistoryMessages` (4), `Chat__CacheAnswers` (true), `Chat__CacheDays` (14) | token-saving layer (see README) |

Every provider limit defaults to **0 = disabled**. Set it to your real free-tier limit or lower. FlowMind stops at that number; it never moves to a paid model.

## Provider limits, resets and the 80% policy
FlowMind enforces its own daily caps and stops before the provider's. During development every cap is set to **80% of the provider's published or reported free limit**, so the remaining 20% absorbs counting differences (estimated vs. real tokens, different reset times, failed calls that still count).

| Provider (job) | Provider's limit | Resets | FlowMind setting (80%) | Notes |
|---|---|---|---|---|
| **Gemini** (analyse documents) | Not published in Google's docs; shown in AI Studio -> *Rate Limit*. Third-party reports for Flash-Lite: about 500 requests/day, 10/min. Limits are per *project* | Midnight **Pacific** (Google docs) | `GEMINI_DAILY_REQUEST_LIMIT=400`, `GEMINI_REQUESTS_PER_MINUTE=8` | **Provisional.** If AI Studio shows a different number, set 80% of that. If it shows 20/day, use 16 |
| **Groq** gpt-oss-20b (chat) | 30 req/min, 1,000 req/day, **8,000 tokens/min, 200,000 tokens/day** ([Groq docs](https://console.groq.com/docs/rate-limits); your plan's exact values are in the console). Per *organization* | Not stated by Groq. FlowMind counts a UTC day | `GROQ_DAILY_TOKEN_LIMIT=160000`, `GROQ_DAILY_REQUEST_LIMIT=800`, `GROQ_REQUESTS_PER_MINUTE=6` | **Tokens/day is the binding limit**, not requests/day. 6/min keeps the 8,000 tokens/min limit safe for ~1,000-token prompts |
| **Cloudflare** bge-m3 (embeddings) | **10,000 neurons/day** free; 1,075 neurons per million input tokens; beyond it calls fail, nothing is billed on the Free plan ([Cloudflare docs](https://developers.cloudflare.com/workers-ai/platform/pricing/)) | **00:00 UTC** ("All limits reset daily at 00:00 UTC") | `CLOUDFLARE_DAILY_NEURON_LIMIT=8000` | 8,000 neurons is about 7.4 million tokens/day: far more than development needs |
| **Mistral OCR** (scanned PDFs) | Free OCR allowance not published; third-party sources say OCR is paid (about $1-2 per 1,000 pages) and the free API tier has general limits of 1 request/s and 1 billion tokens per **month** | Monthly (general limit) | `MISTRAL_DAILY_PAGE_LIMIT=0` (off) | Left off on purpose: enabling it could cost money. Enable only if your Mistral *Limits* page shows a free page allowance, then set 80% of it |

### What each feature consumes (measured on the running system)
| Action | Provider | Measured |
|---|---|---|
| Extract text from PDF/DOCX/TXT | none (local) | 0 tokens |
| Analyse a document | Gemini | one request per up to 12,000 characters: about **0.8-1.4k tokens in, about 1.1k out** each (a 30,000-character PDF = 3-4 requests) |
| Index a document | Cloudflare | about 1 token per 3 characters, charged 1,075 neurons per million tokens: a 3-page document is about 500 tokens (**under 1 neuron**); a 100-page document about 70 neurons |
| Embed a question | Cloudflare | **4-66 tokens** (a fraction of a neuron; FlowMind counts each call as at least 1) |
| Answer a question | Groq | **about 580-610 tokens in, 70 out** on a small document (about 1,100 more when the full 4,500-character excerpt budget is used) |
| Repeat of an earlier question | none | **0 tokens** (answered from the cache) |

### What the 80% settings allow per day
- **Gemini:** 400 analysis requests, roughly 4.8 million characters of documents, hundreds of documents.
- **Groq:** 160,000 tokens: about **240 questions/day** at the small-document level, about **100 questions/day** when every prompt uses its full excerpt budget. Repeated questions cost nothing.
- **Cloudflare:** effectively unlimited for development.
- **Per minute:** Gemini one request every 7.5 s; Groq one every 10 s (a quick second question may wait a few seconds).
## Daily operation
- **Usage:** the *Usage* screen (or `GET /api/v1/usage`) shows today's calls and tokens per provider.
- **Quota reached:** documents wait in *Waiting for AI quota* / *Needs OCR*; after the reset press **Retry** (analysis/OCR) or **Re-index** (search). Finished work is cached and never re-sent. Resets: Gemini midnight Pacific; Cloudflare, Groq and OCR counters midnight UTC.
- **Stuck job:** the worker re-queues jobs left *Running* for more than 15 minutes (`Processing:StaleJobMinutes`). Failed jobs keep a readable error on the document's *Details*.
- **Logs:** application logs go to the host's normal sinks (IIS stdout / service log). SQL text is logged only at Warning+; secrets and document text are not logged.
- **Health:** `/health/live` (process up), `/health/ready` (SQL Server + pgvector reachable). Point your monitor at `/health/ready`.

## Key and secret rotation
- **AI provider keys:** create a new key at the provider, update the environment variable on the API and Worker, recycle the app pool and restart the service, delete the old key. (Do this at once for any key that was ever pasted into chat, email or a ticket.)
- **User access keys:** *Access* screen → Revoke → issue a new one. Effective in ≤15 s.
- **Company admin key:** platform admin screen -> the company -> **Reset key**. Effective at once; give the new key to the company.
- **Disable a company:** platform admin screen -> **Disable**. Sign-in stops immediately and open sessions end within ~15 seconds; data is kept. **Enable** restores access.
- **Delete a company:** platform admin screen -> **Delete**, then type the Company ID. Removes all its data (database, search index, files). Irreversible; take a backup first if in doubt. Usage counters and the audit log remain.
- **Signing key:** change `Auth__SigningKey`, recycle; all sessions end.

## Backups and recovery
Back up together (they reference each other by id): the SQL Server database, the PostgreSQL database, and the storage folder.
- Vectors are **rebuildable**: if the PostgreSQL data is lost, press *Re-index* on each approved document (costs embedding quota; stored chunks are skipped, so a restore from backup saves quota).
- Extracted text and knowledge are in SQL Server; originals are in the storage folder.
- Test a restore before relying on it.

## Upgrades
1. Back up. 2. Stop the Worker. 3. Apply the idempotent migration script. 4. Publish the new API (IIS recycles) and Worker. 5. Start the Worker. 6. Check `/health/ready` and `dotnet test` results from the build. Migrations are additive; new document-status values are appended and never renumbered.

## Troubleshooting
| Symptom | Likely cause |
|---|---|
| App won't start: "Unsafe configuration" | a dev secret/DB/path is still set — message lists each problem |
| Sign-in fails for everyone | wrong `Auth__SigningKey` on one server, or clock skew > 1 min |
| Documents stay *Queued* | Worker not running or cannot reach SQL Server |
| *Waiting for AI quota* immediately | `DailyRequestLimit` is 0 (default) or the key is missing |
| Chat says 429 | the Groq/Cloudflare daily cap you set is reached (see Usage) |
| Chat always "not documented" | document not **approved** yet, or not indexed (check Details → *Embedding* job) |
| `/health/ready` unhealthy for PostgreSQL | server down, wrong password, or `vector` extension missing |
