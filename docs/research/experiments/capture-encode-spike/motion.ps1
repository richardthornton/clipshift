# Motion generator for spike validation: a borderless window on display 0 repainting every frame.
# Without it the desktop is static, DDA delivers nothing, and the spike records black.
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$f = New-Object System.Windows.Forms.Form
$f.FormBorderStyle = 'None'
$f.StartPosition = 'Manual'
$f.Location = New-Object System.Drawing.Point(100, 100)
$f.Size = New-Object System.Drawing.Size(1200, 800)
$f.TopMost = $true
$script:n = 0
$f.Add_Paint({
    param($s, $e)
    $script:n++
    for ($i = 0; $i -lt 40; $i++) {
        $c = [System.Drawing.Color]::FromArgb((($script:n * 7 + $i * 31) % 256), (($script:n * 13 + $i * 11) % 256), (($script:n * 3 + $i * 53) % 256))
        $b = New-Object System.Drawing.SolidBrush $c
        $x = (($script:n * 17 + $i * 97) % 1100)
        $y = (($script:n * 23 + $i * 61) % 700)
        $e.Graphics.FillRectangle($b, $x, $y, 90, 90)
        $b.Dispose()
    }
})
$t = New-Object System.Windows.Forms.Timer
$t.Interval = 8
$t.Add_Tick({ $f.Invalidate() })
$t.Start()
$stop = New-Object System.Windows.Forms.Timer
$stop.Interval = 25000
$stop.Add_Tick({ $f.Close() })
$stop.Start()
[System.Windows.Forms.Application]::Run($f)
