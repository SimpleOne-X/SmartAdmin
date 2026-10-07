@echo off
rem dev-start.bat - one-click start backend + frontend dev env
rem usage: double-click, or run  dev-start.bat  in repo root
rem backend http://localhost:5100 (MinimalHost)
rem   web (Vue)   http://localhost:5173 (Vite)
rem with Windows Terminal: ONE window, two tabs (SmartAdmin-Api / SmartAdmin-Web)
rem without it (wt.exe missing): falls back to two separate cmd windows
setlocal
cd /d "%~dp0"

where wt.exe >nul 2>&1
if errorlevel 1 goto :fallback

echo [dev] starting backend http://localhost:5100 + frontend http://localhost:5173 in one Windows Terminal window ...
wt.exe -w new new-tab --title SmartAdmin-Api --suppressApplicationTitle -d "%~dp0backend" cmd /k "dotnet run --project samples/MinimalHost" ; new-tab --title SmartAdmin-Web --suppressApplicationTitle -d "%~dp0web" cmd /k "npm install && npm run dev"
echo.
echo Started: one terminal window, tab "SmartAdmin-Api" + tab "SmartAdmin-Web". Close a tab to stop that service.
goto :eof

:fallback
echo [dev] wt.exe not found, falling back to separate windows.
echo [api] starting backend http://localhost:5100 ...
start "SmartAdmin-Api" cmd /k "cd /d %~dp0backend && dotnet run --project samples/MinimalHost"

echo [web] starting Vue frontend http://localhost:5173 ...
start "SmartAdmin-Web" cmd /k "cd /d %~dp0web && npm install && npm run dev"

echo.
echo All started in separate windows. Close a window to stop that service.
