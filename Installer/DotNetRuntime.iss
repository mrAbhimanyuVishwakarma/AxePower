// Framework-dependent builds need the x64 10.0 desktop framework and its core runtime.
// Microsoft installs emulated x64 .NET under dotnet\x64 on Arm64 Windows.
function IsDotNetDesktopRuntimeInstalled(): Boolean;
var
  FindRec: TFindRec;
  DotNetRoot, DesktopPath, RuntimePath: String;
  Patch: Integer;
begin
  Result := False;
  DotNetRoot := ExpandConstant('{commonpf64}\dotnet');
  if IsArm64 then
    DotNetRoot := DotNetRoot + '\x64';

  if not FileExists(DotNetRoot + '\dotnet.exe') then
    Exit;

  DesktopPath := DotNetRoot + '\shared\Microsoft.WindowsDesktop.App';
  RuntimePath := DotNetRoot + '\shared\Microsoft.NETCore.App';
  if FindFirst(DesktopPath + '\10.0.*', FindRec) then
  begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
           (Copy(FindRec.Name, 1, 5) = '10.0.') then
        begin
          // Preview and malformed folder names have no numeric patch.
          Patch := StrToIntDef(Copy(FindRec.Name, 6, Length(FindRec.Name)), -1);
          if (Patch >= 15) and
             FileExists(DesktopPath + '\' + FindRec.Name + '\PresentationFramework.dll') and
             FileExists(RuntimePath + '\' + FindRec.Name + '\coreclr.dll') then
          begin
            Result := True;
            Exit;
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function EnsureDotNetDesktopRuntime(): String;
var
  ScriptPath: String;
  ShowMode, ResultCode: Integer;
begin
  Result := '';
  if IsDotNetDesktopRuntimeInstalled() then
    Exit;

  ExtractTemporaryFile('install-dotnet.ps1');
  ScriptPath := ExpandConstant('{tmp}\install-dotnet.ps1');
  if WizardSilent then
    ShowMode := SW_HIDE
  else
    ShowMode := SW_SHOWNORMAL;

  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
              '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '"',
              '', ShowMode, ewWaitUntilTerminated, ResultCode) then
    Log('Could not start the .NET Desktop Runtime prerequisite helper.');

  // Verify that the runtime is actually present before Setup writes any application files.
  if not IsDotNetDesktopRuntimeInstalled() then
  begin
    Result := 'Setup was cancelled because the Microsoft .NET 10 Desktop Runtime (x64), version 10.0.15 or later in the 10.0 series, is required.' + #13#10 + #13#10 +
              'Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run Setup again.';
  end;
end;
