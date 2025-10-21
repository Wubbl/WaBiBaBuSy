# Quick Start: Building the WaBiBaBuSy Installer

## Option 1: Automated Build (Recommended)

### Prerequisites
1. Install **Inno Setup 6**: https://jrsoftware.org/isdl.php

### Build Command
Simply run the PowerShell script:

```powershell
cd Installer
.\Build-Installer.ps1
```

The script will:
- ✅ Clean previous builds
- ✅ Publish the application in Release mode
- ✅ Compile the Inno Setup installer
- ✅ Show you the installer location and size

**Output**: `publish\installer\WaBiBaBuSy-Setup-2.0.0.exe`

---

## Option 2: Manual Build

### Step 1: Publish Application
```powershell
cd C:\Users\Patrick\Documents\GitHub\WaBiBaBuSy
dotnet publish WaBiBaBuSy.UI\WaBiBaBuSy.UI.csproj --configuration Release --output publish
```

### Step 2: Build Installer
1. Open Inno Setup Compiler
2. Open `Installer\WaBiBaBuSy.iss`
3. Press **F9** to compile

---

## Testing the Installer

### On Your Development Machine
```powershell
# Run the installer
.\publish\installer\WaBiBaBuSy-Setup-2.0.0.exe
```

### On a Clean Test Machine
1. Copy `WaBiBaBuSy-Setup-2.0.0.exe` to the test machine
2. Double-click to run
3. Follow the installation wizard
4. The installer will check for .NET 8.0 Desktop Runtime
5. If missing, it will prompt you to download it

---

## What Gets Installed

### Application Files
```
C:\Program Files\WaBiBaBuSy\
├── WaBiBaBuSy.UI.exe (main executable)
├── WaBiBaBuSy.Core.dll
├── WaBiBaBuSy.WallpaperEngine.dll
├── WaBiBaBuSy.Grpc.dll
├── WaBiBaBuSy.Models.dll
├── LibVLCSharp.dll
├── libvlc\ (VLC libraries)
└── (other dependencies)
```

### User Data
```
%LOCALAPPDATA%\WaBiBaBuSy\
├── config.json (created on first run)
├── Cache\ (wallpaper content cache)
└── Logs\ (application logs)
```

### Shortcuts
- Start Menu: `WaBiBaBuSy`
- Desktop (optional)
- Startup folder (optional)

---

## Distribution

The final `WaBiBaBuSy-Setup-2.0.0.exe` is a **single, standalone installer** that includes:
- Application binaries
- .NET runtime check
- Directory creation
- Registry entries
- Uninstaller

**You can distribute this single .exe file** via:
- USB drive
- Email
- Cloud storage (Dropbox, Google Drive, etc.)
- GitHub Releases
- Your own website

---

## Troubleshooting

### "Inno Setup not found"
Download and install from: https://jrsoftware.org/isdl.php

### "dotnet command not found"
Install .NET 8 SDK from: https://dotnet.microsoft.com/download/dotnet/8.0

### ".NET 8 Runtime required" during installation
The installer will prompt the user to download it automatically.
Direct link: https://dotnet.microsoft.com/download/dotnet/8.0/runtime

### Installer won't run on target machine
- Ensure Windows 10 (1809+) or Windows 11
- Run as Administrator if needed
- Check Windows Defender / antivirus didn't block it

---

## Next Steps

After creating the installer:

1. **Test on a clean VM** (Windows 10/11 without .NET 8)
2. **Verify all features work** after installation
3. **Create GitHub Release** and upload the installer
4. **Write user documentation** for your club members
5. **Celebrate!** 🎉 You now have a professional installer!
