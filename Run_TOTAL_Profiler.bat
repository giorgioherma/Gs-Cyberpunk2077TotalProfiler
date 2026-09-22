@echo off
setlocal
cd /d "%~dp0"
where py >nul 2>nul
if %errorlevel%==0 (
  py -3 total_profiler.py
) else (
  python total_profiler.py
)
if errorlevel 1 pause
