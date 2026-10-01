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
    foreach ($t in @('Otto', 'Otto:openai', 'Otto:gemini', 'Otto:xai', 'Otto:deepseek', 'Otto:openrouter', 'Otto:custom', 'Otto:microsoft')) {
        cmdkey /delete:$t | Out-Null
    }
    Write-Host 'Otto and all of its data and saved keys are removed.'
} else {
    Write-Host "Otto is removed. Your notes, routines, sounds and keys are kept (run with -All to remove those too)."
}
