@echo off
rem Double-click to remove Otto (keeps your notes, routines and keys; see uninstall.ps1 -All to remove everything).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
pause
