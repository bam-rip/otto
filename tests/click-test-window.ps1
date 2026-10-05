# Target window for `Otto.exe --click-test` (OTTO_DEV=1): a fake checkout with buttons and a password box.
# Runs as its own process because Otto refuses to click its own windows. Each real click is appended to
# %TEMP%\otto-click-target.txt, so the test can tell which clicks actually landed.
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$log = Join-Path $env:TEMP 'otto-click-target.txt'
Remove-Item $log -ErrorAction SilentlyContinue
$f = New-Object Windows.Forms.Form -Property @{ Text = 'Otto click test'; Width = 420; Height = 260; TopMost = $true; StartPosition = 'CenterScreen' }
$y = 20
foreach ($label in 'Add to cart', 'Place your order') {
    $b = New-Object Windows.Forms.Button -Property @{ Text = $label; Left = 20; Top = $y; Width = 360; Height = 40 }
    $b.Add_Click({ param($s) Add-Content $log "clicked: $($s.Text)" }.GetNewClosure())
    $f.Controls.Add($b); $y += 55
}
$f.Controls.Add((New-Object Windows.Forms.Label -Property @{ Text = 'Password'; Left = 20; Top = $y + 4; Width = 80 }))
$pw = New-Object Windows.Forms.TextBox -Property @{ Name = 'Password'; AccessibleName = 'Password'; UseSystemPasswordChar = $true; Left = 110; Top = $y; Width = 270 }
$f.Controls.Add($pw)
# Windows applies the launcher's window style (hidden/minimized) to the first window shown; undo that
$f.Add_Shown({ $f.WindowState = 'Normal'; $f.Activate() })
$f.Add_FormClosing({ Add-Content $log "password box held: '$($pw.Text)'" })
$t = New-Object Windows.Forms.Timer -Property @{ Interval = 30000 }; $t.Add_Tick({ $f.Close() }); $t.Start() # never linger
[Windows.Forms.Application]::Run($f)
