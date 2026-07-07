; WaBiBaBuSy Installer Script for Inno Setup
; Requires Inno Setup 6.0 or later: https://jrsoftware.org/isinfo.php

#define MyAppName "WaBiBaBuSy"
; MyAppVersion is passed from Build-Installer.ps1 via /D flag; fallback if building manually
#ifndef MyAppVersion
  #define MyAppVersion "2.6.3"
#endif
; MyBuildNumber is passed from Build-Installer.ps1 via /D flag
#ifndef MyBuildNumber
  #define MyBuildNumber "0"
#endif
#define MyAppPublisher "BiBaBu"
#define MyAppURL "https://github.com/pfnetsch/WaBiBaBuSy"
#define MyAppExeName "WaBiBaBuSy.UI.exe"

[Setup]
; NOTE: The value of AppId uniquely identifies this application. Do not use the same AppId value in installers for other applications.
AppId={{8B4E6F2A-1D3C-4A5E-9F7B-2C8D5E6A9B1C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=..\LICENSE.txt
; Uncomment the following line to run in non administrative install mode (install for current user only.)
;PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\publish\installer
OutputBaseFilename=WaBiBaBuSy-v{#MyAppVersion}-build{#MyBuildNumber}-Setup
SetupIconFile=..\WaBiBaBuSy.UI\Assets\BiBaBuColorIcon.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
; File version metadata shown in Explorer properties
VersionInfoVersion={#MyAppVersion}.{#MyBuildNumber}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} - Wallpaper Synchronization System
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCopyright=Copyright (c) 2025-2026 {#MyAppPublisher}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Start WaBiBaBuSy on Windows startup"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Main application files
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; NOTE: Don't use "Flags: ignoreversion" on any shared system files

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
  DotNetInstalled: Boolean;
begin
  // Check if .NET 9.0 runtimes are installed (need both Desktop and ASP.NET Core for gRPC)
  DotNetInstalled := RegKeyExists(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost') or
                      RegKeyExists(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x86\sharedhost');

  if not DotNetInstalled then
  begin
    if MsgBox('.NET 9.0 is required but not installed.' + #13#10 + #13#10 +
              'WaBiBaBuSy requires both the .NET Desktop Runtime AND the ASP.NET Core Runtime (for networking).' + #13#10 + #13#10 +
              'The easiest option is to install the .NET 9.0 SDK which includes everything.' + #13#10 + #13#10 +
              'Would you like to open the download page now?', mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open',
        'https://dotnet.microsoft.com/download/dotnet/9.0',
        '', '', SW_SHOW, ewNoWait, ResultCode);
    end;
    Result := False;
  end
  else
    Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ConfigDir: String;
begin
  if CurStep = ssPostInstall then
  begin
    // Create application data directories
    ConfigDir := ExpandConstant('{localappdata}\WaBiBaBuSy');
    if not DirExists(ConfigDir) then
      CreateDir(ConfigDir);

    ConfigDir := ExpandConstant('{localappdata}\WaBiBaBuSy\Cache');
    if not DirExists(ConfigDir) then
      CreateDir(ConfigDir);

    ConfigDir := ExpandConstant('{localappdata}\WaBiBaBuSy\Logs');
    if not DirExists(ConfigDir) then
      CreateDir(ConfigDir);
  end;
end;

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\WaBiBaBuSy"
