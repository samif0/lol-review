@echo off
echo === LoLReview Rebuild ===
echo.

:: Kill LoLReview if running
taskkill /IM LoLReview.exe /F >nul 2>&1
if %errorlevel%==0 (
    echo Killed LoLReview.exe
    timeout /t 2 /nobreak >nul
) else (
    echo LoLReview not running
)

:: Delete dist folder
if exist dist\LoLReview (
    echo Deleting dist\LoLReview...
    rmdir /s /q dist\LoLReview
    echo Done
) else (
    echo dist\LoLReview not found, skipping
)

echo.

:: Build
echo Building...
.venv\Scripts\python build.py

pause
