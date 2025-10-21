# WaBiBaBuSy Installer

This directory contains the installer configuration for WaBiBaBuSy.

## Prerequisites

1. **Inno Setup 6.0 or later**
   - Download from: https://jrsoftware.org/isdl.php
   - Install the program (free and open source)

## Building the Installer

### Step 1: Publish the Application

Run this from the solution root directory:

```powershell
dotnet publish WaBiBaBuSy.UI\WaBiBaBuSy.UI.csproj --configuration Release --output publish --self-contained false
```

This creates a `publish` folder with all necessary files.

### Step 2: Compile the Installer

1. Open **Inno Setup Compiler**
2. Click **File → Open** and select `WaBiBaBuSy.iss`
3. Click **Build → Compile** (or press F9)

The installer will be created at:
```
publish\installer\WaBiBaBuSy-Setup-2.0.0.exe
```

### Alternative: Command Line Build

If you have Inno Setup installed, you can build from command line:

```powershell
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" WaBiBaBuSy.iss
```

## What the Installer Does

1. **Checks for .NET 8.0 Desktop Runtime**
   - If not installed, prompts user to download it
   - Provides direct link to Microsoft download page

2. **Installs Application Files**
   - Default location: `C:\Program Files\WaBiBaBuSy\`
   - Includes all DLLs, runtime files, and dependencies

3. **Creates Shortcuts**
   - Start Menu: `WaBiBaBuSy`
   - Desktop icon (optional, unchecked by default)
   - Startup folder entry (optional, unchecked by default)

4. **Creates Data Directories**
   - `%LOCALAPPDATA%\WaBiBaBuSy\` - Configuration
   - `%LOCALAPPDATA%\WaBiBaBuSy\Cache\` - Cached wallpaper content
   - `%LOCALAPPDATA%\WaBiBaBuSy\Logs\` - Log files

5. **Adds Uninstaller**
   - Available via Start Menu or Control Panel
   - Removes application files
   - Optionally removes user data

## Customization

Edit `WaBiBaBuSy.iss` to customize:

- **Version Number**: Change `#define MyAppVersion`
- **Publisher Info**: Change `#define MyAppPublisher`
- **Install Location**: Change `DefaultDirName`
- **Icon**: Replace `WaBiBaBuSy.UI\Assets\icon.ico`

## Troubleshooting

### "Cannot open file" error
- Make sure you ran `dotnet publish` first
- Check that `publish` folder exists with WaBiBaBuSy.UI.exe

### ".NET not found" warning
- Install .NET 8.0 Desktop Runtime from:
  https://dotnet.microsoft.com/download/dotnet/8.0/runtime

### "Icon file not found"
- Create an icon file at `WaBiBaBuSy.UI\Assets\icon.ico`
- Or remove the `SetupIconFile` line from the script

## Distribution

The final installer (`WaBiBaBuSy-Setup-2.0.0.exe`) is a single executable that can be:

- Shared via USB drive
- Downloaded from a website
- Distributed via network share
- Uploaded to GitHub releases

**Installer Size**: Approximately 50-100 MB (depending on dependencies)

## System Requirements

- **OS**: Windows 10 (1809+) or Windows 11
- **Runtime**: .NET 8.0 Desktop Runtime (x64)
- **RAM**: 4 GB minimum, 8 GB recommended
- **Disk**: 200 MB for application + cache space
- **Network**: For multi-machine sync functionality
