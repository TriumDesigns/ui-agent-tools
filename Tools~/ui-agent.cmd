@echo off
rem ui-agent (Windows) - forwards to ui-agent.ps1. Usage: ui-agent.cmd <command> [asset-or-method]
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ui-agent.ps1" %*
exit /b %ERRORLEVEL%
