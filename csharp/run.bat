@echo off
cd /d "%~dp0"
start "" "src\LoLReview.App\bin\x64\Debug\net8.0-windows10.0.19041.0\LoLReview.App.exe"
timeout /t 15
type "%LOCALAPPDATA%\LoLReview\startup.log"
pause
