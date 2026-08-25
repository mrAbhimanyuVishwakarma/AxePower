#define AppName "AxePower"
#define AppVersion "1.4"
#define AppPublisher "Abhimanyu Vishwakarma"
#define AppURL "https://github.com/mrAbhimanyuVishwakarma/AxePower_Download"
#define AppExeName "AxePower.App.exe"
#define OutputDir "Output"
#define AppId "{2A4A87C1-1B8D-4F7F-9C8F-7C94829377B8}"

[Setup]
AppId={{#AppId}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL="https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/issues"
AppUpdatesURL="https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/releases"
DefaultDirName={autopf}\{#AppName}
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutputDir}
OutputBaseFilename=AxePower-v1.4-Setup
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
Source: "..\AxePower.App\bin\Release\net8.0-windows\win-x64\publish\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion restartreplace
Source: "..\AxePower.App\bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs restartreplace

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// Closes any running instances of the application
procedure CloseRunningApp();
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Finds the uninstaller string from the Windows registry for existing installations
function GetExistingUninstallString(): string;
var
  sUnInstPath: string;
  sKey: string;
begin
  sKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1';
  sUnInstPath := '';
  
  if not RegQueryStringValue(HKCU, sKey, 'UninstallString', sUnInstPath) then
    RegQueryStringValue(HKLM, sKey, 'UninstallString', sUnInstPath);
    
  Result := sUnInstPath;
end;

// Pre-installation cleanup: terminates running instance and uninstalls previous version cleanly
function PrepareToInstall(var NeedsRestart: Boolean): string;
var
  sUnInstallString: string;
  ResultCode: Integer;
begin
  // 1. Force terminate any running instances
  CloseRunningApp();
  Sleep(500);

  // 2. Check for previously installed version and cleanly uninstall it
  sUnInstallString := GetExistingUninstallString();
  if sUnInstallString <> '' then
  begin
    sUnInstallString := RemoveQuotes(sUnInstallString);
    if FileExists(sUnInstallString) then
    begin
      Exec(sUnInstallString, '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Sleep(1000);
    end;
  end;

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
