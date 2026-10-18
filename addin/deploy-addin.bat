@echo off
rem Revit MCP — © 2026 Rita (Rirychh)
rem https://github.com/Rirychh/revit-mcp
REM ─────────────────────────────────────────────────────────────────────
REM  這支以前自己寫死了一條部署路徑，而且已經過期：
REM    bin\Release\net48\RevitMcpAddin.dll   (2026-08-12, 72KB)
REM  現行建置輸出在
REM    bin\Revit2024\Release\net48\RevitMcpAddin.dll   (694KB)
REM  它還不會重新 build，也沒有複製 RevitMcpAddin.addin manifest，
REM  所以跑下去會靜默把 Revit 外掛降級回 7 週前的版本。
REM
REM  部署邏輯只留一份：deploy-2024.bat（build + 備份 + DLL + manifest）。
REM  這支現在只是轉呼叫，避免同樣的路徑漂移再發生一次。
REM  多版本（2025/2024/2022）一次部署請用 deploy-addin.ps1。
REM ─────────────────────────────────────────────────────────────────────
echo [note] deploy-addin.bat 已改為轉呼叫 deploy-2024.bat（部署邏輯的唯一正本）。
echo.
call "%~dp0deploy-2024.bat" %*
exit /b %ERRORLEVEL%
