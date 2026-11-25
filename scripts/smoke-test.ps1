# End-to-end check of a running FlowMind stack: health -> new test company -> upload a sample -> analysis -> approve -> index -> ask.
# Usage:  ./scripts/smoke-test.ps1              (defaults to http://localhost:8080 and reads the platform key from .env)
# It creates a throwaway company ("smoke-<number>") and prints nothing secret. Needs your AI keys/limits in .env for steps 4-7.
param([string]$Base = 'http://localhost:8080')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$line = Get-Content (Join-Path $root '.env') -ErrorAction Stop | Where-Object { $_ -like 'PLATFORM_ADMIN_KEY=*' }
if (-not $line) { throw "PLATFORM_ADMIN_KEY not found in .env (run scripts/init-env.ps1)" }
$platformKey = ($line -split '=', 2)[1]

function Call($method, $path, $token, $body) {
  $h = @{}; if ($token) { $h.Authorization = "Bearer $token" }
  if ($body) { Invoke-RestMethod -Method $method -Uri "$Base$path" -Headers $h -Body ($body | ConvertTo-Json) -ContentType 'application/json' }
  else { Invoke-RestMethod -Method $method -Uri "$Base$path" -Headers $h }
}
function Step($n, $text) { Write-Host "`n[$n] $text" -ForegroundColor Cyan }
function Ok($text) { Write-Host "    OK  $text" -ForegroundColor Green }
function Warn($text) { Write-Host "    !!  $text" -ForegroundColor Yellow }

Step 1 'Health'
$health = Invoke-RestMethod "$Base/health/ready"
$health.checks | ForEach-Object { if ($_.status -eq 'Healthy') { Ok "$($_.name) $($_.detail)" } else { Warn "$($_.name): $($_.status) $($_.detail)" } }
if ($health.status -ne 'Healthy') { throw 'Not healthy yet - wait a minute and retry, or run: docker compose logs api' }

Step 2 'Create a test company and application'
$n = Get-Random -Maximum 99999
$admin = (Call POST /api/v1/auth/token $null @{ apiKey = $platformKey }).accessToken
$t = Call POST /api/v1/tenants $admin @{ name = "Smoke $n"; slug = "smoke-$n" }
$tok = (Call POST /api/v1/auth/token $null @{ tenantSlug = $t.slug; apiKey = $t.apiKey }).accessToken
$app = Call POST /api/v1/applications $tok @{ name = 'Hospital System' }
Ok "company smoke-$n, application '$($app.name)'"

Step 3 'Upload samples/sample-hospital-guide.txt'
$file = Join-Path $root 'samples\sample-hospital-guide.txt'
$d = (curl.exe -s -H "Authorization: Bearer $tok" -F "ApplicationId=$($app.id)" -F "File=@$file" "$Base/api/v1/documents") | ConvertFrom-Json
Ok "document $($d.documentId)"

Step 4 'Wait for text extraction + AI analysis (up to 3 minutes)'
for ($i = 0; $i -lt 36; $i++) {
  Start-Sleep 5
  $st = Call GET "/api/v1/documents/$($d.documentId)/status" $tok
  if ($st.status -in 'PendingReview', 'Failed', 'AwaitingAnalysis', 'AwaitingOcr') { break }
}
if ($st.status -eq 'PendingReview') { Ok 'analysed, ready for review' }
elseif ($st.status -eq 'AwaitingAnalysis') { Warn "AI analysis is off or its daily limit is reached. Put GEMINI_API_KEY and GEMINI_DAILY_REQUEST_LIMIT in .env, then: docker compose up -d. ($($st.latestJob.error))"; return }
else { Warn "status $($st.status): $($st.latestJob.error)"; return }

Step 5 'Approve the document and wait for indexing'
Call POST "/api/v1/documents/$($d.documentId)/approve" $tok | Out-Null
$indexed = $false
for ($i = 0; $i -lt 20; $i++) {
  Start-Sleep 3
  try { $hits = Call POST /api/v1/search $tok @{ applicationId = $app.id; query = 'phone number' }; if (@($hits).Count -gt 0) { $indexed = $true; break } } catch { $searchError = $_.Exception.Message }
}
if ($indexed) { Ok 'indexed and searchable' } else { Warn "not searchable yet. Put CLOUDFLARE_ACCOUNT_ID / CLOUDFLARE_API_TOKEN / CLOUDFLARE_DAILY_NEURON_LIMIT in .env, then: docker compose up -d  ($searchError)"; return }

Step 6 'Create a user key'
$userKey = (Call POST /api/v1/access-keys $tok @{ name = 'smoke test' }).key
Ok 'user key created (not shown)'

Step 7 'Ask a question: POST /api/ask'
$bodyFile = Join-Path $env:TEMP 'flowmind-ask.json'
[IO.File]::WriteAllText($bodyFile, '{"message":"What validations apply when registering a patient?"}')
$raw = curl.exe -s -w "`nHTTP %{http_code}" -X POST "$Base/api/ask" -H "X-Api-Key: $userKey" -H 'Content-Type: application/json' -d "@$bodyFile"
Remove-Item $bodyFile -ErrorAction SilentlyContinue
$status = ($raw -split "`n")[-1]; $json = ($raw -split "`n")[0]
if ($status -eq 'HTTP 200') {
  $r = $json | ConvertFrom-Json
  Ok "documented = $($r.documented)"
  Write-Host "`n$($r.answer)`n"
  $r.sources | ForEach-Object { Write-Host "    source: $($_.document), page $($_.page)" }
} else { Warn "$status  $json" }

Write-Host "`nDone. Open $Base in a browser to use the web app." -ForegroundColor Cyan
