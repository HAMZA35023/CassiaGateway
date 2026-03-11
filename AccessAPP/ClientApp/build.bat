@echo off
setlocal
cd /d "%~dp0"

echo === BUILD ===
if not exist "node_modules\" (
  echo === NPM INSTALL ===
  call :retry_npm_ci
  if errorlevel 1 exit /b 1
)
call npm run build:wwwroot
echo npm exited with errorlevel=%errorlevel%
if errorlevel 1 exit /b 1
echo DONE
exit /b 0

:retry_npm_ci
for /L %%i in (1,1,3) do (
  echo Attempt %%i of 3...
  call npm ci
  if not errorlevel 1 exit /b 0
  echo Attempt %%i failed. Waiting 15s before retry...
  timeout /t 15 /nobreak >nul
)
echo npm ci failed after 3 attempts.
exit /b 1
