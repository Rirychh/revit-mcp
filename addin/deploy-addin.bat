@echo off
rem Revit MCP - (c) 2026 Rita (Rirychh)
rem https://github.com/Rirychh/revit-mcp
rem ---------------------------------------------------------------------
rem  Thin wrapper. All deployment logic lives in deploy-addin.ps1
rem  (build + backup + DLL + .addin manifest), so there is only one copy
rem  to keep correct. Arguments are passed straight through, e.g.
rem      deploy-addin.bat -Versions 2024,2025
rem      deploy-addin.bat -SkipBuild
rem  With no arguments it deploys to every installed Revit 2022-2027.
rem
rem  Save your work and close Revit first. This never closes Revit for you.
rem  (Kept ASCII-only on purpose: cmd.exe reads .bat files in the system
rem  code page, so non-ASCII text here can break parsing.)
rem ---------------------------------------------------------------------
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy-addin.ps1" %*
set RC=%ERRORLEVEL%
rem Keep the window open only when started by double-click.
echo %cmdcmdline% | find /i "%~nx0" >nul && pause
exit /b %RC%
