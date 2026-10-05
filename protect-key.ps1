# One-time: locks the release signing key (%USERPROFILE%\.otto\release-key.pem) with a passphrase, so a copy of
# the file (a backup, or one taken by malware) is useless without it. publish.ps1 asks for the passphrase when
# signing. Keep the passphrase in a password manager: without it, releases can't be signed.
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$key = Join-Path $env:USERPROFILE '.otto\release-key.pem'
if (-not (Test-Path $key)) { throw "No release key at $key." }
if ((Get-Content $key -Raw) -match 'ENCRYPTED PRIVATE KEY') { Write-Host 'The key is already protected.'; return }

function Plain($s) { [Net.NetworkCredential]::new('', $s).Password }
$a = Read-Host 'New passphrase (12+ characters)' -AsSecureString
$b = Read-Host 'Type it again' -AsSecureString
if ((Plain $a) -ne (Plain $b)) { throw 'The two passphrases differ. Nothing was changed.' }
if ((Plain $a).Length -lt 12) { throw 'Use at least 12 characters. Nothing was changed.' }

Write-Host 'Building...'
dotnet build "$src\Otto.csproj" -c Release --nologo -v quiet
$exe = Get-ChildItem "$src\bin\Release" -Recurse -Filter Otto.exe | Select-Object -First 1
$env:OTTO_DEV = '1'
$env:OTTO_SIGNING_PASSPHRASE = Plain $a
try { Start-Process $exe.FullName -ArgumentList '--protect-signing-key' -Wait }
finally { Remove-Item Env:\OTTO_DEV, Env:\OTTO_SIGNING_PASSPHRASE -ErrorAction SilentlyContinue }

$result = Get-Content "$env:TEMP\otto-protect-key.txt" -ErrorAction SilentlyContinue
if ($result -ne 'protected') { throw "Didn't work: $result" }
Write-Host ''
Write-Host "Done. $key is now locked with your passphrase."
Write-Host 'Next:'
Write-Host '  1. Save the passphrase in your password manager.'
Write-Host '  2. Copy the locked key file to a backup off this PC (USB stick, or attach it in your password manager).'
Write-Host '  3. Delete any unlocked copies, e.g. the one in Downloads.'
