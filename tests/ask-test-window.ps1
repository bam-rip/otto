# Target for `Otto.exe --ask-test` (OTTO_DEV=1): a window with some text already selected.
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$f = New-Object Windows.Forms.Form -Property @{ Text = 'Otto ask test'; Width = 460; Height = 180; TopMost = $true; StartPosition = 'CenterScreen' }
$t = New-Object Windows.Forms.TextBox -Property @{ Multiline = $true; Left = 20; Top = 20; Width = 400; Height = 90; Text = 'Mitochondria are the powerhouse of the cell. Ribosomes make proteins.' }
$f.Controls.Add($t)
$f.Add_Shown({ $f.WindowState = 'Normal'; $f.Activate(); $t.Focus(); $t.Select(0, 44) })
$tm = New-Object Windows.Forms.Timer -Property @{ Interval = 20000 }; $tm.Add_Tick({ $f.Close() }); $tm.Start()
[Windows.Forms.Application]::Run($f)
