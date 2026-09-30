# Generates TestScenarios/assets/marker.png: 64x64, 4 px black border, magenta #FF00FF interior,
# white 12x12 dot at x 40-51 / y 26-37 (right of center -> "Normal" orientation for MarkerDetector).
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\TestScenarios\assets\marker.png'
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
$bmp = New-Object System.Drawing.Bitmap 64, 64
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::Black)
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 0, 255))), 4, 4, 56, 56)
$g.FillRectangle([System.Drawing.Brushes]::White, 40, 26, 12, 12)
$g.Dispose()
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "Wrote $out"
