@echo off
cd /d "%~dp0src\LoLReview.App\bin\x64\Debug\net8.0-windows10.0.19041.0"
"C:\Program Files (x86)\Windows Kits\10\bin\10.0.19041.0\x64\makepri.exe" createconfig /cf priconfig.xml /dq en-US /o
"C:\Program Files (x86)\Windows Kits\10\bin\10.0.19041.0\x64\makepri.exe" new /pr . /cf priconfig.xml /in LoLReview.App /of resources.pri /o
if exist resources.pri (echo SUCCESS) else (echo FAILED)
