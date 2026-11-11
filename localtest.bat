@echo off
setlocal

set "SITE_DIR=%~dp0AppWebsite"
set "PORT=8000"

if not exist "%SITE_DIR%\index.html" (
  echo Error: AppWebsite\index.html was not found.
  pause
  exit /b 1
)

where py >nul 2>nul
if %errorlevel% equ 0 (
  set "PYTHON_CMD=py"
) else (
  where python >nul 2>nul
  if errorlevel 1 (
    echo Error: Python is not installed or is not available in PATH.
    echo Install Python, then run this file again.
    pause
    exit /b 1
  )
  set "PYTHON_CMD=python"
)

echo Starting AxePower website at http://localhost:%PORT%
echo Press Ctrl+C to stop the server.
start "" "http://localhost:%PORT%"
%PYTHON_CMD% -m http.server %PORT% --directory "%SITE_DIR%"

endlocal
