@echo off
rem Zbiera wyniki kolektora dlugiego do zipa (Pulpit + pendrive) i uruchamia go ponownie.
rem Zbierz-Wyniki.cmd -Usun   = dodatkowo wyrejestrowuje zadanie Harmonogramu (koniec zbierania).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Zbierz-Wyniki.ps1" %*
set "diagnozaExit=%errorlevel%"
pause
exit /b %diagnozaExit%
