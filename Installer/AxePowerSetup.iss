[Setup]
AppName=AxePower
AppVersion=2.0.0
DefaultDirName={autopf}\AxePower
DefaultGroupName=AxePower
OutputBaseFilename=AxePower-v2.0.0-Setup
Compression=lzma
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\AxePower.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\AxePower.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\AxePower.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
; Exclude PDB files if any
; NOTE: Don't use "Flags: ignoreversion" on any shared system files

[Icons]
Name: "{group}\AxePower"; Filename: "{app}\AxePower.exe"
Name: "{autodesktop}\AxePower"; Filename: "{app}\AxePower.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\AxePower.exe"; Description: "{cm:LaunchProgram,AxePower}"; Flags: nowait postinstall skipifsilent
