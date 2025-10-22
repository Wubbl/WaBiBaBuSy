# FFmpeg Binaries

This directory should contain the FFmpeg binaries required for video thumbnail generation.

## Quick Setup

1. Download FFmpeg essentials from: https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip
2. Extract the zip file
3. Copy these 3 files from the `bin` folder to this directory:
   - `ffmpeg.exe`
   - `ffprobe.exe`
   - `ffplay.exe` (optional)

## Alternative: Use FFmpeg from System PATH

If you already have FFmpeg installed and in your system PATH, the application will use that instead.

## File List

After setup, this directory should contain:
- ffmpeg.exe (~85 MB)
- ffprobe.exe (~85 MB)
- ffplay.exe (optional)

These files will be automatically copied to the output directory during build.
