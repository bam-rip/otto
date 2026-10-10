# Packs and signs an addon for the "addons" GitHub release: addons-src\<id>\ -> release\addons\<id>.zip (+ .sig)
# Usage: .\publish-addon.ps1 reactions
#   then: gh release upload addons release\addons\reactions.zip release\addons\reactions.zip.sig --clobber
#   (the first time: gh release create addons --prerelease --title "Otto addons" --notes "Optional extras for Otto" <files>)
param([Parameter(Mandatory)][string]$Id)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$from = Join-Path $src "addons-src\$Id"
if (-not (Test-Path (Join-Path $from 'addon.json'))) { throw "No addon at $from (it needs an addon.json)." }
$out = Join-Path $src 'release\addons'
New-Item -ItemType Directory -Force $out | Out-Null
$zip = Join-Path $out "$Id.zip"
Remove-Item $zip, "$zip.sig" -ErrorAction SilentlyContinue
Compress-Archive -Path "$from\*" -DestinationPath $zip

# signed with the release key, like updates: Otto refuses an addon whose signature doesn't match
dotnet build "$src\Otto.csproj" -c Debug --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$exe = Get-ChildItem "$src\bin\Debug" -Recurse -Filter Otto.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $env:OTTO_SIGNING_PASSPHRASE) {
    $secure = Read-Host 'Release key passphrase' -AsSecureString
    $env:OTTO_SIGNING_PASSPHRASE = [Net.NetworkCredential]::new('', $secure).Password
}
try { Start-Process $exe.FullName -ArgumentList '--sign-release', "`"$zip`"" -Wait }
finally { Remove-Item Env:\OTTO_SIGNING_PASSPHRASE -ErrorAction SilentlyContinue }
if (-not (Test-Path "$zip.sig")) { throw "Signing failed: $(Get-Content "$env:TEMP\otto-sign-error.txt" -ErrorAction SilentlyContinue)" }
Write-Host "Built $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
