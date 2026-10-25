<#
  Generates config/ui-offsets.json from config/ui-names.json with raid-extractor's ConfigGen (its existing build), without
  writing anything in raid-extractor: ConfigGen runs on a staging root in output/offsets-staging whose dump/, work/ and
  upstream/ are junctions to raid-extractor's (read only; ConfigGen writes only <root>/config/offsets.json).
  Re-run after a game update, once raid-extractor has a new dump.
#>
param([string]$RaidExtractor = (Join-Path $PSScriptRoot "..\..\raid-extractor"))
$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$re = Resolve-Path $RaidExtractor
$dll = Join-Path $re "src\ConfigGen\bin\Release\net10.0\ConfigGen.dll"
if (-not (Test-Path $dll)) { $dll = Join-Path $re "src\ConfigGen\bin\Debug\net10.0\ConfigGen.dll" }
if (-not (Test-Path $dll)) { throw "ConfigGen is not built in raid-extractor ($dll). Build it there; raid-actions does not build in raid-extractor." }
$newestSource = Get-ChildItem (Join-Path $re "src\ConfigGen") -Filter *.cs | Sort-Object LastWriteTime | Select-Object -Last 1
if ($newestSource.LastWriteTime -gt (Get-Item $dll).LastWriteTime) {
  throw "ConfigGen's source ($($newestSource.Name)) is newer than its build ($dll). Ask for it to be rebuilt in raid-extractor."
}

$staging = Join-Path $repo "output\offsets-staging"
if (Test-Path $staging) {
  # Remove junctions first (never their targets), then the folder.
  foreach ($j in "dump", "work", "upstream") { $p = Join-Path $staging $j; if (Test-Path $p) { cmd /c rmdir "$p" | Out-Null } }
  Remove-Item -Recurse -Force $staging
}
New-Item -ItemType Directory -Force (Join-Path $staging "config") | Out-Null
foreach ($j in "dump", "work", "upstream") { cmd /c mklink /J (Join-Path $staging $j) (Join-Path $re $j) | Out-Null }
Copy-Item (Join-Path $repo "config\ui-names.json") (Join-Path $staging "config\names.json")

dotnet $dll $staging
if ($LASTEXITCODE -ne 0) { throw "ConfigGen failed (exit $LASTEXITCODE); config/ui-offsets.json not changed." }

$out = Join-Path $repo "config\ui-offsets.json"
Copy-Item (Join-Path $staging "config\offsets.json") $out -Force
foreach ($j in "dump", "work", "upstream") { cmd /c rmdir (Join-Path $staging $j) | Out-Null }
"Wrote $out (ConfigGen $((Get-FileHash $dll -Algorithm SHA256).Hash.Substring(0,12)), game $((Get-Content $out | ConvertFrom-Json).build.gameVersion))"
