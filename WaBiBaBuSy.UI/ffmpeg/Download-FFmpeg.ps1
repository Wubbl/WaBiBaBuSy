# Download FFmpeg Essentials Binaries
# This script downloads FFmpeg binaries needed for video thumbnail generation

$ErrorActionPreference = "Stop"

$ffmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
$zipFile = "ffmpeg-essentials.zip"
$extractPath = "ffmpeg-temp"

Write-Host "Downloading FFmpeg essentials..." -ForegroundColor Cyan
Invoke-WebRequest -Uri $ffmpegUrl -OutFile $zipFile -UseBasicParsing

Write-Host "Extracting..." -ForegroundColor Cyan
Expand-Archive -Path $zipFile -DestinationPath $extractPath -Force

Write-Host "Copying binaries..." -ForegroundColor Cyan
$binPath = Get-ChildItem -Path $extractPath -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
$binDir = $binPath.DirectoryName

Copy-Item "$binDir\ffmpeg.exe" -Destination "." -Force
Copy-Item "$binDir\ffprobe.exe" -Destination "." -Force
Write-Host "  Copied: ffmpeg.exe" -ForegroundColor Green
Write-Host "  Copied: ffprobe.exe" -ForegroundColor Green

Write-Host "Cleaning up..." -ForegroundColor Cyan
Remove-Item $zipFile -Force
Remove-Item $extractPath -Recurse -Force

Write-Host "`nFFmpeg binaries installed successfully!" -ForegroundColor Green
Write-Host "Files are ready in: $(Get-Location)" -ForegroundColor Green
