@echo off
cd /d "%~dp0"
if not exist ".venv\Scripts\python.exe" goto :novenv
".venv\Scripts\python.exe" "run.py"
echo.
echo [process exited] If there is a Traceback above, please send it to me.
pause
exit /b 0
:novenv
echo [ERROR] .venv not found. Please create the venv and install PySide6 first.
pause
exit /b 1