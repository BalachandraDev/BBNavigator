# BBNavigator on Windows / IIS: deployment guide

Status: written from the project's code and settings. It has **not yet been run end to end on a Windows Server**. Do the first install on a test server and tick every check in section 9 before a hospital relies on it.

## 1. What runs where

| Part | Runs as | Notes |
|---|---|---|
| Web app + API + the website files | IIS site (in-process, app pool "No Managed Code") | One site, no CORS. |
| Background worker (text extraction, OCR, embeddings) | Windows Service `FlowMind.Worker` | A separate process, not in the app pool. Already built to run as a Windows Service. |
| SQL Server | Real instance (Standard/Enterprise, or Express for small sites: 10 GB per database) | Holds companies, documents, chats, settings. |
| PostgreSQL 16 + pgvector | Windows install, or a small Linux server / managed Postgres | Holds the search vectors. See section 4. |
| Uploaded files | Folder on this server, or a file share | See section 6. |

Both the site and the worker must use the **same** connection strings, the **same** `Security:SecretsKey` and the **same** file storage location.

## 2. Install prerequisites (once)

1. Windows Server 2019 or later, IIS with the *Web Server* role.
2. **.NET 8 Hosting Bundle** (not just the runtime). Then run `iisreset`.
3. A TLS certificate for the site name (the app refuses plain http: `Security:RequireHttps` is true).
4. SQL Server reachable from this server. PostgreSQL with pgvector (section 4).

## 3. Publish and create the site

```powershell
dotnet publish src/FlowMind.API    -c Release -o C:\inetpub\flowmind
dotnet publish src/FlowMind.Worker -c Release -o C:\flowmind\worker
```

1. In IIS Manager create an **app pool** `BBNavigator`: .NET CLR version **No Managed Code**.
2. Create the **site** pointing at `C:\inetpub\flowmind`, bind **https** with your certificate.
3. The pool identity needs **Read & execute** on `C:\inetpub\flowmind`, and **Modify** on the storage folder (section 6) and on `C:\inetpub\flowmind\logs` if you turn on stdout logging.
4. Upload limit: the app allows 25 MB (`Upload:MaxBytes`). IIS allows about 30 MB by default. If you raise one, raise both (`requestLimits maxAllowedContentLength` in `web.config`).

## 4. Databases

**SQL Server**: create an empty database `BBNavigator` and a login for the app. Either let the app create the tables on first start (`Database__AutoMigrate = true`, the login needs rights to alter the schema) or give the DBA the script:

```powershell
dotnet ef migrations script --idempotent -p src/FlowMind.Infrastructure -s src/FlowMind.API -o deploy.sql
```

For upgrades run the new idempotent script (or keep AutoMigrate on). Back up before every upgrade.

**PostgreSQL + pgvector**: create database `flowmind_vectors` and a user `flowmind`. pgvector is an extension: on Windows it has to be built or installed for your PostgreSQL version (follow the Windows section of the pgvector project's own instructions). If that is not allowed on the server, run PostgreSQL on a small Linux machine or a managed service that includes pgvector, and point the connection string at it. Then run `db/postgres/001_init.sql` as a user allowed to `CREATE EXTENSION`; the app creates the table itself afterwards. The vector size is 1024 (BGE-M3) and must match the AI provider setting.

## 5. Settings (secrets are never put in files inside the web folder)

Set these as environment variables for the IIS site. The simplest reliable place is the `<aspNetCore>` element in `C:\inetpub\flowmind\web.config`, then restrict that file so only Administrators and the pool identity can read it:

```xml
<aspNetCore processPath="dotnet" arguments=".\FlowMind.API.dll" hostingModel="inprocess">
  <environmentVariables>
    <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
    <environmentVariable name="ConnectionStrings__SqlServer" value="Server=SQLHOST;Database=BBNavigator;User Id=flowmind_app;Password=...;TrustServerCertificate=False;MultipleActiveResultSets=true" />
    <environmentVariable name="ConnectionStrings__Postgres"  value="Host=PGHOST;Database=flowmind_vectors;Username=flowmind;Password=..." />
    <environmentVariable name="Storage__RootPath"            value="D:\FlowMindData" />
    <environmentVariable name="Auth__SigningKey"             value="(32+ random characters)" />
    <environmentVariable name="Auth__PlatformAdminKey"       value="(24+ random characters)" />
    <environmentVariable name="Security__SecretsKey"         value="(32+ random characters)" />
    <environmentVariable name="Database__AutoMigrate"        value="true" />
    <environmentVariable name="AiProviders__Gemini__ApiKey"  value="..." />
    <!-- add Cloudflare, Groq and their daily limits the same way, see appsettings.json -->
  </environmentVariables>
</aspNetCore>
```

Generate random values with `[Convert]::ToBase64String((1..48 | % { [byte](Get-Random -Max 256) }))`. **Keep a copy of `Security__SecretsKey` and `Auth__SigningKey` somewhere safe**: losing the secrets key means stored company keys and cloud credentials can no longer be read.

The app refuses to start in Production if a secret is missing, too short, or still a development placeholder. If the site shows HTTP 500.30, run `dotnet FlowMind.API.dll` from the folder once and read the message.

## 6. Where uploaded files are kept

- **Default**: `Storage__RootPath` (for example `D:\FlowMindData`), on a disk that is backed up. The worker must see the same folder, so run it on the same machine or use a UNC path both accounts can reach.
- **Folder or file share per company**: sign in as platform admin, **Companies, Server folders for company files**, approve `D:\FlowMindDocs` or `\\fileserver\BBNavigator` (it must exist; a test file is written to prove permissions). Each company admin then types a folder inside it in **Settings, Storage**, and can copy or move the files already uploaded.
- **Permissions**: the account that runs the app pool **and** the account that runs the worker both need **Modify** on that folder. For a file share, grant both the share permission and the NTFS permission to those accounts (use a domain or managed service account for the pool and the worker so the share accepts them).

## 7. Run the worker as a Windows Service

```powershell
sc.exe create FlowMind.Worker binPath= "C:\flowmind\worker\FlowMind.Worker.exe" start= delayed-auto obj= "DOMAIN\svc-flowmind" password= "..."
sc.exe failure FlowMind.Worker reset= 86400 actions= restart/60000/restart/60000/restart/60000
```

Give the service the **same environment variables** as the site (connection strings, `Security__SecretsKey`, `Storage__RootPath`, AI keys). Services read them from the registry value `Environment` (multi-string) under `HKLM\SYSTEM\CurrentControlSet\Services\FlowMind.Worker`, or from an `appsettings.Production.json` next to the exe that only Administrators and the service account can read. Then `sc.exe start FlowMind.Worker`.

## 8. Backups and routine care

Back up together, nightly, and test a restore:
1. SQL Server database `BBNavigator`.
2. PostgreSQL database `flowmind_vectors` (the vectors can be rebuilt by re-processing, but restoring is faster).
3. The storage folder(s): `Storage__RootPath` and every approved server folder. These hold the original uploads and are the **only copy**.
4. The secret values (offline, access controlled).

Also: Windows and .NET Hosting Bundle updates, certificate renewal, disk space on the storage drive, and a look at **Settings, Security check** after each change.

## 9. Go-live checks

- [ ] `https://host/health/live` and `https://host/health/ready` return healthy.
- [ ] Platform admin can sign in; a test company can be created.
- [ ] Upload a small **synthetic** document: it reaches "Ready", and a chat answers from it (this proves the worker, both databases and the AI keys).
- [ ] The file exists in the storage folder; restart the server and it still opens.
- [ ] Share link opens on a phone; a company without access cannot see another company's documents.
- [ ] `FlowMind.Worker` restarts by itself after you stop it.
- [ ] A backup was restored on a spare machine.
- [ ] Hospital data stays out until the hospital has approved each external AI provider's terms (Gemini, Cloudflare, Groq): until then use synthetic documents only.

`scripts/smoke-test.ps1` runs a quick API check (see its header for the arguments).
