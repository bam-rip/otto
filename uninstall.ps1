# Removes Otto: the program, its shortcuts and start-with-Windows entry.
# Your notes, routines, sounds, backups and saved keys are kept unless you pass -All.
param([switch]$All)
$ErrorActionPreference = 'SilentlyContinue'

Get-Process Otto | Stop-Process -Force
Start-Sleep -Milliseconds 500
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\Otto') -Recurse -Force
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Otto
foreach ($dir in @([Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop'))) {
    Remove-Item (Join-Path $dir 'Otto.lnk')
}

if ($All) {
    Remove-Item (Join-Path $env:LOCALAPPDATA 'Otto') -Recurse -Force
    Remove-Item 'HKCU:\Software\Otto' -Recurse -Force
    # saved API keys and the email sign-in, in Windows Credential Manager
    # every Otto entry: API keys (and extra keys like Otto:gemini#2), email sign-ins (Otto:microsoft, Otto:imap)
    $targets = cmdkey /list | Select-String 'Target:\s*(?:LegacyGeneric:target=)?(Otto(?::[^\s]*)?)\s*$' | ForEach-Object { $_.Matches[0].Groups[1].Value }
    foreach ($t in $targets) { cmdkey /delete:$t | Out-Null }
    Write-Host 'Otto and all of its data and saved keys are removed.'
} else {
    Write-Host "Otto is removed. Your notes, routines, sounds and keys are kept (run with -All to remove those too)."
}
