# CLAUDE.md / AGENTS.md - zasady pracy w repozytorium VNC Alert

Oba pliki maja identyczna tresc (pilnuje hak `pre-commit`). Zmieniasz jeden - kopiujesz na drugi
w tym samym commicie.

## 1. Repozytorium

Program "VNC Alert" (nazwa techniczna `MaszynyAlert`): aplikacja WPF w zasobniku Windows, ktora
przez RFB (tylko odczyt) obserwuje ekrany frezarek, wykrywa bledy maszyn i zglasza alarmy.
Kod .NET 10 w solution `src\MaszynyAlert.sln`, instalator Inno Setup, wydania przez GitHub Actions.
Opis architektury: `docs\architektura.md`.

| Katalog / plik | Zawartosc |
|---|---|
| `src\MaszynyAlert.App` | aplikacja WPF (zasobnik, okna, uslugi) |
| `src\MaszynyAlert.Core` | rdzen bez zaleznosci od Windows: klient RFB, detekcja, reguly, alerty, klaster UDP |
| `src\MaszynyAlert.Windows` | implementacje zalezne od Windows: OCR, DPAPI, autostart, dzwiek |
| `src\MaszynyAlert.Export` | eksport raportu postojow do PDF i Excela |
| `src\MaszynyAlert.Probe` | narzedzie rozpoznawcze: wersja RFB, zabezpieczenia, zrzuty, OCR, UDP |
| `tests\*.Tests`, `tests\InstallerProbe`, `tests\samples` | testy xUnit, sonda instalatora, probki ekranow |
| `tools\` | narzedzia pomocnicze (diagnoza hali, higiena testow, testy dlugotrwale) |
| `scripts\` | budowanie paczki i instalatora, podpisywanie, testy paczki i instalatora |
| `installer\` | skrypty Inno Setup (`MaszynyAlert.iss`, `MaszynyAlert-wydanie.iss`) i skrypty instalacyjne |
| `config\` | `config.example.json`, `rules.example.json`, `test-ekran\` |
| `sounds\` | dzwieki alarmow |
| `.github\workflows\` | `ci.yml`, `wydanie.yml` |
| `.githooks\`, `.claude\` | haki git i haki narzedzia (sekcja 5) |

## 2. Budowanie i testy lokalnie

- SDK: `global.json` (10.0.100, `rollForward: latestFeature`).
- `testy.ps1` (Windows): `dotnet restore`, `build -c Release` i `test` calej solution
  (wyniki trx w `TestResults\Windows`). `testy.sh` (Linux): `dotnet test -c Release` projektow
  `MaszynyAlert.Core.Tests` i `MaszynyAlert.Rfb.Tests`.
- Wersja (`Directory.Build.props`): lokalnie `0.4.0-dev`; w wydaniu `0.4.<N>`, gdzie
  `N = VncAlertBuildNumberOffset (270) + numer przebiegu wydanie.yml`, a `0.4` to `VncAlertMajorMinor`.
- Na komputerze MSI polityka kontroli aplikacji blokuje wczytanie niepodpisanych bibliotek testowych
  poza katalogiem runnera (`C:\actions-runner\_work`) - tam testy weryfikuje CI, nie przebieg lokalny.

## 3. CI i wydania

- `ci.yml` (pull_request, push na `main`, recznie): wola reusable
  `mbuasm/serwer-laptop/.github/workflows/testy.yml@main` z `typ: desktop`. Joby:
  `testy / testy-linux` (runner `msi-linux`, `testy.sh`), `testy / testy-windows`
  (`msi-windows`, `testy.ps1`), `testy / skan-sekretow`. SDK z `global.json` instaluje workflow.
- Rulesety na `main`: `main` (zakaz usuwania, zakaz non-fast-forward, zmiany tylko przez PR)
  i `testy` (wymagane trzy checki `testy / *`, strict - galaz aktualna wzgledem `main`).
- `wydanie.yml`, job `sprawdz`: reusable `ui-sprawdz.yml`; uruchamia sie na PR (nie draft) i recznie.
  Runner z wejscia `runs_on` albo zmiennej repo `SPRAWDZ_RUNS_ON` (VM `ui-msi-1`, etykieta `ui-msi`;
  pusta = `windows-latest`). Odmowa kodem 77 dla innego repo i PR z forka. Buduje paczke
  (`scripts\Przygotuj-Wydanie.ps1`), uruchamia smoke startu, test instalatora i testy
  `MaszynyAlert.Windows.Tests` w sesji interaktywnej. Nie jest wymaganym checkiem.
- `wydanie.yml`, job `wydanie` (po `sprawdz`, tylko `workflow_dispatch` na `main`):
  `Przygotuj-Wydanie.ps1` -> instalator `installer/MaszynyAlert-wydanie.iss` -> `wydanie-zglos`
  na serwerze (skan sekretow) -> `/srv/wydania/maszyny-alert` = `https://wydania.mbuas.pl/maszyny-alert/`
  (`INDEX.md` i `VncAlert-Setup-<wersja>.exe`). Stanowiska pobieraja aktualizacje z `updates.url`.
- Uruchomienie wydania (wlasciciel): `gh workflow run wydanie -R mbuasm/VNC-Alert --ref main`.
- Runnery sa w grupach organizacji `msi` (`msi-linux`, `msi-windows`) i `msi-ui` (`ui-msi-1`);
  grupy dopuszczaja tylko wskazane repo i reusable workflowy z `main`
  (zarzadza `windows\runner-grupa.ps1` w repo serwera).

## 4. Zasady pracy agenta (bezwzgledne)

a) Galaz zawsze od aktualnego `main`, krotka nazwa techniczna. PR wylacznie jako draft:
   `gh pr create --draft --title "<temat>" --body ""` - opis pusty. Agent nigdy nie oznacza PR
   jako gotowego do recenzji, nie scala, nie robi force push, nie pisze na `main` i nie usuwa
   cudzych galezi. "Ready for review" i scalenie robi wylacznie wlasciciel.
b) Commit: jedna linia tematu po polsku bez znakow diakrytycznych, max 60 znakow, bez tresci,
   bez stopek (np. `Co-Authored-By`), bez emoji. Tytul PR = temat commita. Na GitHub trafia
   wylacznie kod: zadnych komentarzy, opisow, podsumowan ani notatek w PR, commitach
   i nazwach galezi.
c) Zadnych nowych plikow informacyjnych w repo (notatki, raporty, statusy, plany, TODO,
   podsumowania pracy). Takie dokumenty powstaja tylko lokalnie poza repo (np. `..\notatki\`
   obok klonu) i nigdy nie sa commitowane. W repo wolno zmieniac kod, testy, konfiguracje,
   skrypty, workflow, `docs\architektura.md` (gdy zmienia sie architektura) i te dwa pliki
   zasad (zawsze oba razem).
d) Komentarz w kodzie opisuje tylko to, co kod robi - nigdy zmiane, historie, przyczyne
   poprawki, daty, recenzje, numery PR lub zadan. Nigdzie w repo nie wspominaj innych
   repozytoriow, narzedzi AI, modeli ani przebiegu pracy.
e) Zero sekretow w repo (hasla, tokeny, klucze). `updates.url` w `config\config.example.json`
   zmienia sie tylko decyzja wlasciciela.
f) Zmiana kodu = odpowiadajacy test. Nie wylaczaj testow (`Skip`) bez decyzji wlasciciela.
   Zielone `testy / *` w CI sa warunkiem scalenia.
g) Pliki repo i komunikaty programu po polsku; w C# i JSON zachowaj istniejacy styl ze znakami
   diakrytycznymi. W `.ps1` i `.sh` zachowaj kodowanie i konce linii istniejacych plikow.

## 5. Haki

- Po klonie: `git config core.hooksPath .githooks`.
- `.githooks\commit-msg`: odrzuca komunikat z wiecej niz jedna niepusta linia, temat dluzszy
  niz 60 znakow, znaki spoza ASCII, `Co-Authored-By`, odwolania `#<numer>` i zakazane slowa.
- `.githooks\pre-commit`: odrzuca commit, gdy `CLAUDE.md` rozni sie od `AGENTS.md`, gdy dochodzi
  nowy plik `*.md`/`*.txt` spoza listy (`CLAUDE.md`, `AGENTS.md`, `docs/architektura.md`,
  `src/MaszynyAlert.App/LICENSES-THIRD-PARTY.txt`, `config/test-ekran/CZYTAJ-TO.txt`, `tests\**`) albo gdy
  dodane linie zawieraja zakazane tresci (stopki, nazwy narzedzi, `TODO:` z data, zakazane identyfikatory).
- `.claude\settings.json` (Claude Code): `SessionStart` ustawia `core.hooksPath`, a `PreToolUse`
  (`.claude\hooks\blokada.sh`) blokuje kodem 2 force push, push na `main`, `gh pr merge`
  i `gh pr ready`.
- Hakow nie omijaj (`--no-verify`). Odrzucenie = popraw commit, nie hak.
