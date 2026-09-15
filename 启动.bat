@echo off
cd /d "%~dp0"
if not exist ".venv\Scripts\pythonw.exe" goto :novenv
start "" ".venv\Scripts\pythonw.exe" "run.py"
exit /b 0
:novenv
echo [ERROR] .venv not found. Please create the venv and install PySide6 first.
pause
exit /b 1