; Wspolny workflow wola ISCC z samym /O<katalog>, bez /D - dlatego sciezka paczki i nazwa pliku
; sa ustawione tutaj, a reszta pochodzi z MaszynyAlert.iss bez zmian.
;  - PublishDir: publish\app w korzeniu repo (tworzy go scripts\Przygotuj-Wydanie.ps1);
#define PublishDir AddBackslash(SourcePath) + "..\publish\app"
#define OutputBaseName "VncAlert-Setup-" + GetFileVersion(PublishDir + "\MaszynyAlert.App.exe")
#include "MaszynyAlert.iss"
