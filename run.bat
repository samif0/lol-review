@echo off
cd /d "%~dp0"
start "" "src\Revu.App\bin\x64\Debug\LoLReview.App.exe"
timeout /t 15
type "%LOCALAPPDATA%\Revu\startup.log"
pause
