# One-click install: builds Otto into %LOCALAPPDATA%\Programs\Otto, adds Start menu + desktop
# shortcuts, starts it with Windows, and launches it. Run again any time to update after code changes.
$ErrorActionPreference = 'Stop'
$src  = $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Otto'
$exe  = Join-Path $dest 'Otto.exe'
$run  = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

Write-Host 'Building Otto...'
Get-Process Otto, Jarvis -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
dotnet publish "$src\Otto.csproj" -c Release -o $dest --nologo -v quiet -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'Build failed (is the .NET 8 SDK installed?).' }

# tidy up the old "Jarvis" install, if there is one (settings and data move over on first start)
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\Jarvis') -Recurse -Force -ErrorAction SilentlyContinue
Remove-ItemProperty $run -Name Jarvis -ErrorAction SilentlyContinue

$shell = New-Object -ComObject WScript.Shell
foreach ($dir in @([Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop'))) {
    Remove-Item (Join-Path $dir 'Jarvis.lnk'), (Join-Path $dir 'OTTO.lnk') -ErrorAction SilentlyContinue
    $lnk = $shell.CreateShortcut((Join-Path $dir 'Otto.lnk'))
    $lnk.TargetPath = $exe
    $lnk.Arguments = '--show'
    $lnk.WorkingDirectory = $dest
    $lnk.Description = 'Otto - laptop assistant (Ctrl+Shift+J)'
    $lnk.Save()
}

# start quietly in the tray at login
Set-ItemProperty $run -Name Otto -Value "`"$exe`""

Start-Process $exe '--show'
Write-Host "Done. Otto is installed in $dest, starts with Windows, and is running now (Ctrl+Shift+J)."
