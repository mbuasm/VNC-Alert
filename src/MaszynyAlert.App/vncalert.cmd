@echo off
rem Oficjalna komenda obslugi VNC Alert. Instalator dodaje katalog programu do PATH,
rem wiec "vncalert killapp" dziala z dowolnego okna wiersza polecen.
rem Bez polskich znakow diakrytycznych: konsola bywa uruchamiana w roznych stronach kodowych.
setlocal
set "EXE=%~dp0MaszynyAlert.App.exe"

if /I "%~1"=="killapp" goto killapp
echo VNC Alert - komendy:
echo   vncalert killapp   - zatrzymuje dzialajacy program (ikona znika z zasobnika)
exit /b 2

:killapp
if not exist "%EXE%" (
  echo BLAD: nie znaleziono "%EXE%".
  exit /b 1
)
"%EXE%" --stop
set "CODE=%ERRORLEVEL%"
if "%CODE%"=="0" (
  echo VNC Alert jest zatrzymany.
) else (
  echo BLAD: VNC Alert nadal dziala po 10 sekundach. Sprobuj ponownie albo zakoncz proces
  echo MaszynyAlert.App.exe w Menedzerze zadan.
)
exit /b %CODE%
