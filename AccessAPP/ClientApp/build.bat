@echo off
setlocal
cd /d "%~dp0"

echo === BUILD ===
if not exist "node_modules\" (
  echo === NPM INSTALL ===
  call npm ci
  echo npm ci exited with errorlevel=%errorlevel%
  if errorlevel 1 exit /b 1
)
call npm run build:wwwroot
echo npm exited with errorlevel=%errorlevel%
if errorlevel 1 exit /b 1
echo DONE
