@echo off
setlocal
set "KOMP=%~1"
if "%KOMP%"=="" set "KOMP=1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Diagnoza-Hali.ps1" -Komputer %KOMP% %2 %3 %4 %5 %6
echo.
echo Kod wyjscia: %ERRORLEVEL%
pause
