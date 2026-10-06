#include "AppVersion.iss"

[Setup]
AppName=AxePower
AppVersion={#AppVersion}
DefaultDirName={autopf}\AxePower
DefaultGroupName=AxePower
OutputBaseFilename=AxePower-v{#AppVersion}-Setup
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
; LAN Drop firewall rule: inbound TCP 5050 for AxePower only, on Private and Domain networks.
; Public stays under Windows' default inbound block. Delete first so upgrades never duplicate it.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""AxePower LAN Drop"""; Flags: runhidden waituntilterminated; StatusMsg: "Configuring LAN Drop firewall rule..."
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""AxePower LAN Drop"" dir=in action=allow program=""{app}\AxePower.exe"" protocol=TCP localport=5050 profile=private,domain enable=yes"; Flags: runhidden waituntilterminated; StatusMsg: "Configuring LAN Drop firewall rule..."
Filename: "{app}\AxePower.exe"; Description: "{cm:LaunchProgram,AxePower}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""AxePower LAN Drop"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveLanDropFirewallRule"
