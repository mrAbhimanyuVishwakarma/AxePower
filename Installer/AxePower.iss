#define AppName "AxePower"
#define AppVersion "2.1.0"
#define AppPublisher "Abhimanyu Vishwakarma"
#define AppURL "https://github.com/mrAbhimanyuVishwakarma/AxePower_Download"
#define AppExeName "AxePower.exe"
#define OutputDir "Output"
#define AppId "{2A4A87C1-1B8D-4F7F-9C8F-7C94829377B8}"

[Setup]
; {{ is Inno's escape for a literal brace — a trailing "}" here would corrupt the AppId
AppId={{#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL="https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/issues"
AppUpdatesURL="https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/releases"
DefaultDirName={autopf}\{#AppName}
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
; Admin is needed to install machine-wide and to add the .NET runtime — the app itself
; then runs as a standard user.
PrivilegesRequired=admin
; AxePower is published win-x64, so install into the real Program Files, not the x86 one
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=AxePower-v{#AppVersion}-Setup
SetupIconFile=..\AxePower.App\Resources\AxePower_white.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no
AppMutex=AxePowerSingleInstanceMutex,AxePower_App_Mutex_2A4A87C1

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Prerequisite helper — extracted to {tmp} at run time, never installed with the app
Source: "install-dotnet.ps1"; Flags: dontcopy
Source: "..\AxePower.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion restartreplace
Source: "..\AxePower.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs restartreplace
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; v1.7 and earlier shipped the launcher as AxePower.App.exe — drop the stale files
; on upgrade so only AxePower.exe is left behind.
Type: files; Name: "{app}\AxePower.App.exe"
Type: files; Name: "{app}\AxePower.App.dll"
Type: files; Name: "{app}\AxePower.App.deps.json"
Type: files; Name: "{app}\AxePower.App.runtimeconfig.json"
Type: files; Name: "{app}\AxePower.App.pdb"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; runasoriginaluser: Setup is elevated, but AxePower must start as the normal user
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
// AxePower targets net8.0-windows, which requires the 8.x desktop shared framework
// specifically — a newer major runtime does not satisfy it by default.
function IsDotNetDesktopRuntimeInstalled(): Boolean;
var
  FindRec: TFindRec;
  BasePath: String;
begin
  Result := False;
  BasePath := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');

  if not DirExists(BasePath) then
    Exit;

  if FindFirst(BasePath + '\8.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

// Runs the prerequisite installer in a visible PowerShell window so the user can watch
// the download and install progress instead of staring at a Setup that looks frozen.
// The window closes itself, then Setup carries on.
function InstallDotNetRuntime(): Boolean;
var
  ScriptPath: String;
  ShowMode: Integer;
  ResultCode: Integer;
begin
  ExtractTemporaryFile('install-dotnet.ps1');
  ScriptPath := ExpandConstant('{tmp}\install-dotnet.ps1');

  // Keep it hidden for unattended/silent installs, visible for normal ones
  if WizardSilent then
    ShowMode := SW_HIDE
  else
    ShowMode := SW_SHOWNORMAL;

  Exec('powershell.exe',
       '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '"',
       '', ShowMode, ewWaitUntilTerminated, ResultCode);

  // Trust the machine state, not the exit code
  Result := IsDotNetDesktopRuntimeInstalled();
end;
// Closes any running instances of the application
procedure CloseRunningApp();
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Runs one previous installation's uninstaller and waits for it to finish.
procedure UninstallIfPresent(RootKey: Integer; const KeyName: String);
var
  UninstString, ExePath: String;
  ResultCode, i: Integer;
begin
  if not RegQueryStringValue(RootKey,
       'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + KeyName,
       'UninstallString', UninstString) then
    Exit;

  ExePath := RemoveQuotes(UninstString);
  if not FileExists(ExePath) then
    Exit;

  Exec(ExePath, '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // An Inno uninstaller relaunches itself from %TEMP% and the first process returns
  // straight away, so wait until the real uninstaller is actually gone (max 30s).
  for i := 1 to 60 do
  begin
    if not FileExists(ExePath) then
      Break;
    Sleep(500);
  end;
end;

// Deletes an install folder left behind by a version whose uninstaller was missing.
procedure RemoveLeftoverFolder(const Dir: String);
begin
  if (Dir <> '') and DirExists(Dir) and (CompareText(Dir, ExpandConstant('{app}')) <> 0) then
    DelTree(Dir, True, True, True);
end;

// Removes every previous AxePower install, whichever key and scope it registered under.
// Older setups emitted the AppId with a stray trailing brace, and the very first script
// registered under the plain app name, so all three spellings are checked in both the
// 32- and 64-bit registry views and for both all-users and per-user installs.
procedure RemovePreviousVersions();
var
  Roots: array[0..3] of Integer;
  Keys: array[0..2] of String;
  r, k: Integer;
begin
  Roots[0] := HKLM64;
  Roots[1] := HKLM32;
  Roots[2] := HKCU64;
  Roots[3] := HKCU32;

  Keys[0] := '{#AppId}_is1';
  Keys[1] := '{#AppId}}_is1';   // legacy: AppId once ended with an extra brace
  Keys[2] := 'AxePower_is1';    // legacy: original script defined no AppId

  for r := 0 to 3 do
    for k := 0 to 2 do
      UninstallIfPresent(Roots[r], Keys[k]);

  // Anything still on disk from an install whose uninstaller was already deleted
  RemoveLeftoverFolder(ExpandConstant('{commonpf32}\{#AppName}'));
  RemoveLeftoverFolder(ExpandConstant('{localappdata}\Programs\{#AppName}'));

  // Stale shortcuts from either scope; the [Icons] section recreates the current ones
  DeleteFile(ExpandConstant('{commondesktop}\{#AppName}.lnk'));
  DeleteFile(ExpandConstant('{userdesktop}\{#AppName}.lnk'));
  DeleteFile(ExpandConstant('{commonprograms}\{#AppName}.lnk'));
  DeleteFile(ExpandConstant('{userprograms}\{#AppName}.lnk'));
end;

// Pre-installation cleanup: terminates running instance and uninstalls previous version cleanly
function PrepareToInstall(var NeedsRestart: Boolean): string;
begin
  // 0. Install the .NET runtime first if it is missing. This runs before any files are
  //    copied, and covers silent installs too (where wizard pages never appear).
  if not IsDotNetDesktopRuntimeInstalled() then
  begin
    if not InstallDotNetRuntime() then
    begin
      // Suppressible so an unattended install answers itself instead of hanging on a dialog
      if SuppressibleMsgBox('AxePower needs the Microsoft .NET 8 Desktop Runtime, but it could not be installed.'#13#10#13#10 +
                'Continue installing AxePower anyway?'#13#10 +
                'AxePower will not start until the runtime is installed from:'#13#10 +
                'https://dotnet.microsoft.com/download/dotnet/8.0',
                mbConfirmation, MB_YESNO, IDYES) = IDNO then
      begin
        Result := 'Setup was cancelled because the .NET 8 Desktop Runtime is required.';
        Exit;
      end;
    end;
  end;

  // 1. Force terminate any running instances
  CloseRunningApp();
  Sleep(500);

  // 2. Remove every previously installed version so only this one remains
  RemovePreviousVersions();

  Result := '';
end;

// Pre-step safety check before copying files
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    CloseRunningApp();
  end;
end;
