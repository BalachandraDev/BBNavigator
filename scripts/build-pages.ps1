<#
  Builds the GitHub Pages demo into ./_site.

  GitHub Pages can only host static files, so the demo is the real front end (src/FlowMind.API/wwwroot) plus demo/demo.js, which answers the
  app's API calls from recorded sample data instead of a server. The production app is not changed: this script only copies and adjusts files.

    pwsh scripts/build-pages.ps1                # writes ./_site
    pwsh scripts/build-pages.ps1 -Out C:\temp\site
#>
param([string]$Out = '_site')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src  = Join-Path $root 'src/FlowMind.API/wwwroot'
$demo = Join-Path $root 'demo'
if (-not [IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $root $Out }
$utf8 = New-Object System.Text.UTF8Encoding($false)

if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Force $Out | Out-Null
Copy-Item "$src/*" $Out -Recurse
New-Item -ItemType Directory -Force "$Out/demo" | Out-Null
Copy-Item "$demo/demo.js", "$demo/demo.css", "$demo/fixtures.json" "$Out/demo"
New-Item -ItemType File -Force "$Out/.nojekyll" | Out-Null      # serve every file as is

function Edit-File([string]$path, [scriptblock]$change) {
  $text = [IO.File]::ReadAllText($path, $utf8)
  $new = & $change $text
  if ($new -eq $text) { throw "Nothing changed in $path (the build script is out of date)." }
  [IO.File]::WriteAllText($path, $new, $utf8)
}

# The site lives at https://<user>.github.io/<repo>/, so addresses that start with "/" must become relative.
foreach ($page in 'index.html', 'bot.html') {
  Edit-File "$Out/$page" {
    param($t)
    $t = $t -replace '(href|src)="/', '$1="./'
    $t = $t -replace '</head>', "<link rel=`"stylesheet`" href=`"./demo/demo.css`">`n</head>"
    # demo.js must run before the app's own script so that it is in place before the first request
    $t -replace '(<script type="module")', "<script src=`"./demo/demo.js`"></script>`n`$1"
  }
}
Edit-File "$Out/js/views/share.js" {
  param($t)
  $t = $t.Replace('${location.origin}/bot.html#t=', '${location.href.split(''#'')[0].replace(/[^/]*$/, '''')}bot.html#t=')
  $t.Replace("'/bot.html#preview'", "'./bot.html#preview'").Replace("'/bot.html?v='", "'./bot.html?v='")
}

Write-Host ("Built {0} ({1} files)" -f $Out, (Get-ChildItem $Out -Recurse -File).Count)
