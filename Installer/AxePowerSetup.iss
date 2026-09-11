[Setup]
AppName=AxePower
AppVersion=1.7.0
DefaultDirName={autopf}\AxePower
DefaultGroupName=AxePower
OutputBaseFilename=AxePower-v1.7.0-Setup
Compression=lzma
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\AxePower.App\bin\Release\net8.0-windows\win-x64\publish\AxePower.App.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\AxePower.App\bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Exclude PDB files if any
; NOTE: Don't use "Flags: ignoreversion" on any shared system files

[Icons]
Name: "{group}\AxePower"; Filename: "{app}\AxePower.App.exe"
Name: "{autodesktop}\AxePower"; Filename: "{app}\AxePower.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\AxePower.App.exe"; Description: "{cm:LaunchProgram,AxePower}"; Flags: nowait postinstall skipifsilent
