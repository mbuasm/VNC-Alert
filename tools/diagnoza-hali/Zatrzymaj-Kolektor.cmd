@echo off
rem Lagodnie konczy tylko wlasny kolektor, bez zbierania wynikow i bez usuwania zadania.
rem Po ponownym zalogowaniu zadanie wystartuje znowu; zeby je usunac na stale: Zbierz-Wyniki.cmd -Usun.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Zatrzymaj-Kolektor.ps1" %*
set "diagnozaExit=%errorlevel%"
pause
exit /b %diagnozaExit%
