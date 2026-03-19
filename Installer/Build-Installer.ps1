# WaBiBaBuSy Installer Build Script
# This script automates the process of building the installer

param(
    [string]$Configuration = "Release",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

# Get script directory and solution root
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$SolutionRoot = Split-Path -Parent $ScriptDir
$PublishDir = Join-Path $SolutionRoot "publish"
$InstallerOutputDir = Join-Path $PublishDir "installer"

# Auto-detect version from csproj and git commit count
if (-not $Version) {
    $CsprojPath = Join-Path $SolutionRoot "WaBiBaBuSy.UI\WaBiBaBuSy.UI.csproj"
    [xml]$Csproj = Get-Content $CsprojPath
    $Major = $Csproj.Project.PropertyGroup[0].MajorVersion
    $Minor = $Csproj.Project.PropertyGroup[0].MinorVersion
    $Patch = $Csproj.Project.PropertyGroup[0].PatchVersion
    try {
        $BuildNumber = (git -C $SolutionRoot rev-list --count HEAD 2>$null).Trim()
    } catch {
        $BuildNumber = "0"
    }
    if (-not $BuildNumber) { $BuildNumber = "0" }
    $Version = "$Major.$Minor.$Patch.$BuildNumber"
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " WaBiBaBuSy Installer Build Script" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Version: $Version" -ForegroundColor Cyan
Write-Host "Solution Root: $SolutionRoot" -ForegroundColor Gray
Write-Host "Publish Directory: $PublishDir" -ForegroundColor Gray
Write-Host ""

# Step 1: Clean previous build
Write-Host "[1/4] Cleaning previous build..." -ForegroundColor Yellow
if (Test-Path $PublishDir) {
    Remove-Item $PublishDir -Recurse -Force
    Write-Host "  Removed old publish directory" -ForegroundColor Gray
}
Write-Host "  Done" -ForegroundColor Green
Write-Host ""

# Step 2: Publish the application
Write-Host "[2/4] Publishing application..." -ForegroundColor Yellow
$ProjectPath = Join-Path $SolutionRoot "WaBiBaBuSy.UI\WaBiBaBuSy.UI.csproj"

dotnet publish $ProjectPath `
    --configuration $Configuration `
    --output $PublishDir `
    --self-contained false `
    /p:PublishSingleFile=false `
    /p:DebugType=None `
    /p:DebugSymbols=false `
    /p:BuildNumber=$($Version.Split('.')[-1])

if ($LASTEXITCODE -ne 0) {
    Write-Host "  Failed to publish application!" -ForegroundColor Red
    exit 1
}
Write-Host "  Published to: $PublishDir" -ForegroundColor Green
Write-Host ""

# Step 3: Check for Inno Setup
Write-Host "[3/4] Checking for Inno Setup..." -ForegroundColor Yellow
$InnoSetupPaths = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)

$InnoSetupPath = $null
foreach ($path in $InnoSetupPaths) {
    if (Test-Path $path) {
        $InnoSetupPath = $path
        break
    }
}

if (-not $InnoSetupPath) {
    Write-Host "  Inno Setup not found!" -ForegroundColor Red
    Write-Host "  Please install Inno Setup 6 from: https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
    Write-Host "  Then run this script again." -ForegroundColor Yellow
    exit 1
}

Write-Host "  Found Inno Setup at: $InnoSetupPath" -ForegroundColor Green
Write-Host ""

# Step 4: Build installer
Write-Host "[4/4] Building installer..." -ForegroundColor Yellow
$IssPath = Join-Path $ScriptDir "WaBiBaBuSy.iss"

& $InnoSetupPath "/DMyAppVersion=$Version" $IssPath

if ($LASTEXITCODE -ne 0) {
    Write-Host "  Failed to build installer!" -ForegroundColor Red
    exit 1
}

# Find the generated installer
$InstallerFile = Get-ChildItem $InstallerOutputDir -Filter "WaBiBaBuSy-Setup-*.exe" | Select-Object -First 1

if ($InstallerFile) {
    Write-Host "  Installer built successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host " BUILD SUCCESSFUL!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Installer Location:" -ForegroundColor Yellow
    Write-Host "  $($InstallerFile.FullName)" -ForegroundColor White
    Write-Host ""
    Write-Host "Installer Size:" -ForegroundColor Yellow
    Write-Host "  $([math]::Round($InstallerFile.Length / 1MB, 2)) MB" -ForegroundColor White
    Write-Host ""
} else {
    Write-Host "  Warning: Could not find generated installer file" -ForegroundColor Yellow
}

Write-Host "Press any key to exit..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
