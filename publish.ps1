# Builds the download for a GitHub release: one self-contained Otto.exe (no .NET install needed),
# zipped with the README and license, in .\release\Otto-<version>.zip
# Usage: .\publish.ps1            (then: gh release create v<version> release\Otto-<version>.zip release\Otto-<version>.zip.sig)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$version = ([xml](Get-Content "$src\Otto.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$out = Join-Path $src 'release'
$stage = Join-Path $out "Otto-$version"

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$src\Otto.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $stage --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item "$src\README.md", "$src\LICENSE" $stage

$zip = Join-Path $out "Otto-$version.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$stage\*" -DestinationPath $zip

# sign it with the release key (in %USERPROFILE%\.otto, never in the repo); Otto won't install an unsigned update
if (-not $env:OTTO_SIGNING_PASSPHRASE) {
    $secure = Read-Host 'Release key passphrase' -AsSecureString
    $env:OTTO_SIGNING_PASSPHRASE = [Net.NetworkCredential]::new('', $secure).Password
}
$env:OTTO_DEV = '1'
Remove-Item "$zip.sig" -ErrorAction SilentlyContinue
try { Start-Process "$stage\Otto.exe" -ArgumentList '--sign-release', "`"$zip`"" -Wait }
finally { Remove-Item Env:\OTTO_DEV, Env:\OTTO_SIGNING_PASSPHRASE -ErrorAction SilentlyContinue }
if (-not (Test-Path "$zip.sig")) { throw "Signing failed: $(Get-Content "$env:TEMP\otto-sign-error.txt" -ErrorAction SilentlyContinue)" }
Write-Host "Built $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
