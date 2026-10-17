# Architektura VNC Alert

## 1. Przeznaczenie programu

VNC Alert obserwuje ekrany HMI maszyn (frezarek) przez protokół RFB (VNC), łącząc się z nimi wyłącznie
w trybie odczytu obrazu. Błąd maszyny wykrywa po kolorze: udział czerwonych pikseli na pasku statusu
(`RedTextDetector`, progi HSV z profilu ekranu) musi zostać potwierdzony filtrem „2 z 3” (`RedEpisodeFilter`).
Treść komunikatu odczytuje OCR i opisuje ją słownikiem błędów (`MessageMapper`, plik `rules.json`).
Poza błędami program zgłasza brak łączności z maszyną, postój bez pracy (pasek postępu na 100 % albo na 0 %)
i wyświetlenie na HMI innego ekranu niż główny (`AlertKind`: `Error`, `Offline`, `MachineIdle`, `WrongScreen`).
Błąd maszyny pokazuje okno z dźwiękiem na wszystkich komputerach operatorów, aż ktoś potwierdzi go
przyciskiem „Idę sprawdzić”. Zdarzenia trafiają do historii (`history.csv`), postoje do dziennika (`downtime.jsonl`),
z którego Panel administracyjny buduje raport postojów z eksportem do PDF i Excela.

Nazewnictwo: produkt nazywa się „VNC Alert” (`Product` w `Directory.Build.props`, nazwa w instalatorze),
a nazwa techniczna rozwiązania to `MaszynyAlert`. Nosi ją rozwiązanie `src/MaszynyAlert.sln`, przestrzenie
nazw `MaszynyAlert.*`, plik `MaszynyAlert.App.exe`, katalog danych `%ProgramData%\MaszynyAlert`
(`AppPaths`) i muteks pojedynczej instancji `Global\MaszynyAlert.App` (`Program.SingleInstanceMutexName`).

## 2. Projekty rozwiązania

Wspólne ustawienia wszystkich projektów są w `Directory.Build.props` (m.in. `Nullable`, `ImplicitUsings`,
`EnableWindowsTargeting`, dzięki któremu projekty `net10.0-windows` kompilują się też na Linuksie).
SDK wskazuje `global.json` (`10.0.100`, `rollForward: latestFeature`).

| Projekt | TFM | Rola | `ProjectReference` |
|---|---|---|---|
| `MaszynyAlert.Core` | `net10.0` | Logika bez zależności od Windows: klient RFB, analiza obrazu, OCR (abstrakcja), reguły, alerty, klaster UDP, konfiguracja, historia, aktualizacje | — |
| `MaszynyAlert.Windows` | `net10.0-windows10.0.19041.0` | Integracja z Windows: ścieżki danych, DPAPI, OCR Windows, dźwięk, autostart, zapora, zadania Harmonogramu | `Core` |
| `MaszynyAlert.Export` | `net10.0` | Eksport raportu postojów do PDF (PDFsharp-MigraDoc) i Excela (ClosedXML) | `Core` |
| `MaszynyAlert.App` | `net10.0-windows10.0.19041.0` (`WinExe`, WPF) | Program okienkowy: host DI, okna, ikona w zasobniku, orkiestracja | `Core`, `Windows`, `Export` |
| `MaszynyAlert.Probe` | `net10.0-windows10.0.19041.0` (`Exe`, `AssemblyName` = `probe`) | Narzędzie rozpoznawcze i diagnostyczne `probe.exe` | `Core`, `Windows` |

Projekty testowe (xUnit) w katalogu `tests`:

| Projekt | TFM | `ProjectReference` |
|---|---|---|
| `MaszynyAlert.Core.Tests` | `net10.0` | `Core` |
| `MaszynyAlert.Rfb.Tests` | `net10.0` | `Core` |
| `MaszynyAlert.Export.Tests` | `net10.0` (dodatkowo pakiet `PdfPig`) | `Export` |
| `MaszynyAlert.Probe.Tests` | `net10.0-windows10.0.19041.0` | `Probe`, `Core` |
| `MaszynyAlert.Windows.Tests` | `net10.0-windows10.0.19041.0` | `Windows` |
| `MaszynyAlert.App.Tests` | `net10.0-windows10.0.19041.0` | `App` |

Poza rozwiązaniem leży `tests/InstallerProbe` (`Exe`, referencja do `Windows`): osobny proces pomocniczy
testu instalatora, który przygotowuje i sprawdza dane (`seed`, `verify`, `update`, `vnc-password`,
`operator`) i nie uruchamia aplikacji ani połączeń VNC.

## 3. Struktura katalogów kodu źródłowego

### `src/MaszynyAlert.Core`

- `Abstractions` — interfejsy i drobne typy wspólne.
  - `ISecretStore` — magazyn sekretów (hasła VNC, klucz sieciowy); implementacja Windows to `DpapiSecretStore`.
  - `InMemorySecretStore` — magazyn w pamięci dla testów i Linuksa.
  - `IClock`, `SystemClock` — źródło czasu (UTC i licznik monotoniczny).
  - `AdminPassword` — hash i weryfikacja hasła Panelu administracyjnego.
  - `SecretKeys` — nazwy kluczy w magazynie sekretów.
- `Rfb` — klient protokołu RFB.
  - `RfbClient` — uzgadnia wersję i zabezpieczenia, wysyła żądania aktualizacji i dekoduje ramki.
  - `Framebuffer` — żywy obraz ekranu, do którego piszą dekodery `RawDecoder`, `CopyRectDecoder`, `HextileDecoder`.
  - `RsaAesHandshake`, `RsaAesStream` (z `Rfb/Crypto`: `AesEax`, `AesCmac`) — uwierzytelnienie i szyfrowanie RSA-AES.
  - `VncDes` — szyfrowanie wyzwania VncAuth.
  - `RfbSecurityType` — obsługiwane typy: `None`, `VncAuth`, `RA2`, `RA2ne`, `RA2_256`, `RA2ne_256`.
- `Imaging` — obrazy rastrowe bez zewnętrznych bibliotek.
  - `RgbaImage` — podstawowy obraz RGBA8888.
  - `Gray8Image` — obraz w skali szarości, wejście OCR.
  - `PngCodec` — samodzielny dekoder i koder PNG.
  - `RectInt` — prostokąt ROI i prostokąty aktualizacji RFB.
- `Vision` — analiza obrazu.
  - `RedTextDetector` — liczy udział czerwonych pikseli wg `RedThresholds` profilu.
  - `RedEpisodeFilter` — filtr „N z M” (domyślnie okno 3, wymagane 2).
  - `ProgressBarReader`, `ProgressNumberReader` — odczyt postępu pracy z paska albo liczby (OCR).
  - `MainScreenDetector` — rozpoznaje ekran główny HMI po etykietach (OCR pełnej klatki).
  - `ProgramStateDetector`, `ImageOps` — stan programu (w tym „Abort”) i przygotowanie wycinków do OCR.
- `Ocr` — słownik błędów i instrukcje.
  - `IOcrEngine`, `NullOcrEngine` — abstrakcja silnika OCR i silnik pusty.
  - `SerializingOcrEngine` — kolejkuje wywołania OCR do jednego naraz dla całej aplikacji.
  - `MessageMapper` — mapuje tekst OCR na `MappedMessage` (reguły regex, frazy rozmyte, kody alarmów `AlarmCodeTable`).
  - `TextNormalizer`, `Levenshtein`, `TextReadability` — normalizacja, dopasowanie rozmyte, ocena czytelności tekstu.
  - `AlarmInstructionStore`, `SharedInstructions`, `AlarmInstructionLayers` — instrukcje naprawy: plik, zbiór wspólny grupy, scalanie warstw.
- `Machines` — obserwacja maszyn.
  - `MonitorSupervisor` — uruchamia i zatrzymuje komplet monitorów, sprząta katalog zrzutów (w kodzie: „nadzorca maszyn”).
  - `MachineMonitor` — pętla jednej maszyny: pobranie obrazu, detekcja, OCR, wywołania `AlertStateMachine`.
  - `VncScreenSource` — źródło obrazu przez `RfbClient` w trybie `Persistent` albo `Polling` (`ConnectionMode`).
  - `FolderScreenSource` — źródło testowe: plik `<machineId>.png` w katalogu `test-ekran`.
  - `FileCaptureStore` — zapis zrzutów `{machineId}_{yyyyMMdd-HHmmss}_{tag}.png`.
- `Alerts` — cykl życia alertu.
  - `AlertStateMachine` — epizody `Active` → `Acknowledged` → `Escalated` → `Closed`, zdarzenia `AlertEvent`.
  - `AckMemory` — pamięć potwierdzeń niezależna od tego, który komputer obserwuje.
  - `AlertSoundSchedule` — dźwięk na początku epizodu i co `alertSoundRepeatMs`.
  - `StateMerger`, `AlertVisibility` — scalanie list alertów i reguła widoczności w oknie alertu.
- `Cluster` — komunikacja między komputerami.
  - `ClusterRuntime` — stały punkt wejścia aplikacji; wybiera `ClusterNode` albo `GroupClusterNode`.
  - `UdpPeerBus`, `HmacSigner`, `MessageCodec` — transport UDP, podpis HMAC-SHA256, kodowanie wiadomości.
  - `WatcherRoleController`, `Election`, `PeerTable` — wybór obserwatora.
  - `GroupIdentity` — publiczna tożsamość grupy (bez klucza) i porównanie zgodności.
- `Config` — konfiguracja.
  - `AppConfig` — korzeń `config.json`; sekcje `ClusterConfig`, `TimingConfig`, `OcrConfig`, `PathsConfig`, `UiConfig`, `DiagnosticsConfig`, `UpdatesConfig`.
  - `ConfigLoader` — wczytanie, zapis i walidacja `config.json` i `rules.json`.
  - `GroupProfile`, `GroupFileCodec`, `ProfileHashSchema` — profil grupy i jego odcisk.
  - `GroupSettingsStore`, `GroupApplySession` — trwałe generacje ustawień grupy i ich zastosowanie.
  - `SharedConfigDocument`, `SharedConfigStore`, `SharedConfigSync` — wspólna konfiguracja z Panelu.
- `History` — historia i raporty.
  - `HistoryWriter` — dopisuje zdarzenia do `history.csv`.
  - `DowntimeJournal` — dziennik epizodów postoju `downtime.jsonl`, scalany między komputerami.
  - `ObservationJournal` — przedziały obserwacji maszyn (`state/observation.jsonl`).
  - `DowntimeReport`, `ReportCalendar` — krojenie postojów na dni i zmiany, sumy.
- `Export` — dane wspólne dla eksportu: `DowntimeExportData` (wiersze i sumy), `ReportPresentationNames`.
- `Updates` — aktualizacje: `HttpUpdateSource`, `ReleaseCatalog`, `UpdateCoordinator`, `UpdateApproval`, `UpdateTaskContract` z `UpdateTaskVerifier`.
- `Diagnostics` — `DiagnosticsExporter` (paczka ZIP ze źródeł wybranych allowlistą; klasa nie zna konfiguracji ani magazynu sekretów) i `BuildIdentity` (wersja i commit).

### `src/MaszynyAlert.Windows`

- `AppPaths` — katalogi programu i danych, tryb zainstalowany albo przenośny.
- `DpapiSecretStore` — magazyn sekretów `secrets.dat` szyfrowany DPAPI.
- `WindowsOcrEngine` — OCR systemu Windows (`Windows.Media.Ocr`).
- `WavSoundPlayer` — odtwarzanie plików WAV.
- `GroupSettingsClient`, `GroupFirewall`, `NetworkProfileHelper` — pomocnik z UAC dla ustawień grupy, reguła zapory, profil sieci.
- `StopRequest`, `PreviewActivation`, `SharedConfigReloadRequest` — lokalne sygnały między procesami (zatrzymanie, otwarcie Podglądu, przeładowanie wspólnej konfiguracji).
- `WatchdogTask`, `UpdateTask`, `Autostart`, `PowerModeWatcher` — watchdog, zadanie aktualizacji, wpis `Run`, zdarzenia uśpienia.

### `src/MaszynyAlert.App`

- Korzeń: `Program` (argumenty startowe, muteks, budowa hosta i rejestracja usług), `App.xaml`, `vncalert.cmd`.
- `Services`:
  - `AppOrchestrator` — `BackgroundService` spinający klaster, nadzorcę maszyn, maszynę stanów alertów, historię i dzienniki.
  - `TrayIconService` — ikona w zasobniku i otwieranie okien (Panel tylko po zalogowaniu).
  - `UpdateService`, `WindowsUpdateInstaller`, `UpdateTaskRegistration`, `UpdateTaskCommand` — aktualizacje.
  - `InstalledSetup` — osobny edytor ustawień wersji zainstalowanej, bez hosta, OCR i połączeń VNC/UDP.
  - `AlarmHelpService`, `DiagnosticsService` — instrukcje naprawy i paczka diagnostyczna.
- `ViewModels`: `AlertWindowViewModel` (pozycje okna alertu, `ShouldPlaySound`), `MachineCardViewModel` (karta maszyny),
  `MultiComputerViewModel` (kreator pracy na kilku komputerach z kontrolą zapory), `SharedConfigViewModel`,
  `UpdateViewModel`, `IdlePopupViewModel`, `ScreenPopupViewModel`.
- `Views`: `AlertWindow` („UWAGA, błąd maszyny”), `OperatorWindow` („Podgląd”), `StatusWindow` („Panel administracyjny”
  z zakładkami „Raport postojów”, „Instrukcje naprawy”, „Praca na kilku komputerach”, „Ustawienia”, „Podgląd techniczny”),
  `SetupWindow` („Ustawienia”), `DowntimeView`, `UpdateWindow`, `OverlayWindow` (nakładka statusu), `AdminLoginWindow`;
  podkatalog `Views/Charts` zawiera wykresy `BarChartView`, `LineChartView`, `PieChartView`.
- `Converters`, `Assets` — konwerter widoczności i ikona aplikacji.

### `src/MaszynyAlert.Export`

- `DowntimePdfExporter` — raport PDF (MigraDoc) z czcionką z `ExportFontResolver`.
- `DowntimeExcelExporter` — skoroszyt z arkuszami „Epizody”, „Sumy”, „Błędy”, „Informacje”.
- `ReportFileWriter`, `ExportOutcome` — zapis przez plik tymczasowy i podmianę; wynik bez wyjątku.

### `src/MaszynyAlert.Probe`

- Korzeń: `Program` (wybór komendy), `ProbeSession`, `ConnectionHelper`, `Report`, `GlobalOptions`.
- `Commands` — klasy komend `probe.exe` (interfejs `ICommand`) i pomocnicze (`HostPortArg`, `SimpleTable`, `SummaryPrinter`); opis w sekcji 10.
- `Diagnoza` — kolektor diagnostyki hali: `MachineCollector`, `DlugiKolektor`, `PortScanner`, `FrameStore`, `DiskBudget`.

## 4. Przepływ danych

```
 maszyna (serwer VNC)
      |  RFB: klient wysyła tylko SetPixelFormat, SetEncodings, FramebufferUpdateRequest
      v
 RfbClient -> VncScreenSource            (FolderScreenSource, gdy istnieje test-ekran/<id>.png)
      |  wycinek: StatusBarRoi (+ obszar postępu); pełna klatka co fullFrameIntervalMs
      v
 MachineMonitor (jeden na maszynę, tylko na obserwatorze)
      |-- RedTextDetector -> RedEpisodeFilter(3, 2) ----------> epizod czerwieni
      |-- ImageOps.PrepareForOcr -> SerializingOcrEngine -> MessageMapper (rules.json)
      |-- ProgressBarReader / ProgressNumberReader / MainScreenDetector / ProgramStateDetector
      v
 AlertStateMachine (TimingConfig)  -- AlertEvent: Raised, Acknowledged, Escalated, Closed, Updated
      |                                   |
      |                                   +--> AppOrchestrator --> HistoryWriter (history.csv)
      |                                   |                    --> DowntimeJournal (downtime.jsonl)
      v                                   v
 ClusterRuntime --UDP (HMAC)--> pozostałe komputery: StateMessage co stateIntervalMs
      |
      v
 AlertWindow: AlertSoundSchedule + WavSoundPlayer (sounds/alarm.wav); „Idę sprawdzić” -> AckMessage
      |
 DowntimeView: DowntimeReport.Build -> DowntimeExportData -> DowntimePdfExporter / DowntimeExcelExporter
```

Połączenie jest tylko do odczytu. `RfbClient` definiuje wyłącznie komunikaty klienta typu 0 (`SetPixelFormat`),
2 (`SetEncodings`) i 3 (`FramebufferUpdateRequest`); w kodzie źródłowym nie ma komunikatów `KeyEvent`,
`PointerEvent` ani `ClientCutText`. Schowek przysłany przez serwer jest odczytywany i odrzucany.
`VncScreenSource` łączy się z flagą `Shared = true`, więc nie wymusza rozłączenia innych klientów.
Kodowania zgłaszane serwerowi to domyślnie `Hextile`, `CopyRect` i `Raw` (`RfbClientOptions.Encodings`).

Detekcja: `MachineMonitor.ProcessFrameAsync` analizuje wycinek paska statusu w każdym cyklu
(`pollIntervalMs`). Potwierdzenie filtrem otwiera epizod: zapis zrzutu z tagiem `blad`, odczyt OCR
(do `ocrMaxAttempts` prób, kolejne przy zmianie treści nie częściej niż co `ocrRetryIntervalMs`). Odczyt
(`ReadErrorAsync`) ma do trzech podejść: z maską czerwieni, na oryginalnym obrazie i na kolorowym polu tekstu.
Jeśli baza błędów profilu ma regułę z kategorią `bez alarmu` (`Rule.SuppressedCategory`), monitor czeka
z alarmem na pierwszy odczyt treści (`MessageMapper.HasSuppressRules`); na pozostałych maszynach alarm podnosi
sam kolor. Gdy profil ma włączoną sekcję `mainScreen`, `MainScreenDetector` co `mainScreenCheckIntervalMs`
sprawdza pełną klatkę, a wynik „inny ekran niż główny” `AlertStateMachine.OnMainScreenState` zamienia
w alert `WrongScreen`.

Maszyna stanów: `AlertStateMachine` zamyka epizod po `episodeCloseAfterMs` bez czerwieni, łączy krótkie przerwy
(`episodeBreakAfterMs`, `sameMessageRejoinMs`), po potwierdzeniu eskaluje, gdy błąd trwa dłużej niż
`escalationAfterAckMs`, a brak łączności zgłasza po `offlineAlertAfterMs`. Postój bez pracy zgłasza po
`idleAlertAfterMs` (100 %) albo `idleAtZeroAfterMs` (0 %). Zdarzenia są emitowane poza blokadą.

Dźwięk i potwierdzenie: `AlertWindow` odtwarza `sounds/alarm.wav` na początku epizodu i co `alertSoundRepeatMs`
(`AlertSoundSchedule`); pop-up postoju używa `sounds/idle-ping.wav`. `AppOrchestrator.AcknowledgeAsync`
zapisuje potwierdzenie lokalnie i rozsyła je przez `ClusterRuntime.SendAckAsync`.

Historia i postoje: `AppOrchestrator.OnAlertsMachineChanged` dopisuje każde zdarzenie do `HistoryWriter`
i przekazuje alerty rodzaju `Error` i `MachineIdle` (`DowntimeJournal.IsRecordedKind`) do `DowntimeJournal`;
epizody maszyn obserwowanych z plików testowych nie trafiają do dziennika. `DowntimeReport.Build` kroi
epizody na dni zmian (dzień zaczyna się o 06:00, `ReportCalendar.ShiftDayStartHour`), pomija weekend od soboty
06:00 do poniedziałku 06:00 i oznacza postoje dłuższe niż godzina (`MaxEpisode`). Raport pokazuje czas
zmierzony z dziennika. Eksport (`ReportFileWriter.SavePdf`, `SaveExcel`) wywołuje `DowntimeView`.

## 5. Konfiguracja i dane

`AppPaths.IsInstalled` jest prawdziwe, gdy plik `.exe` leży pod `%ProgramFiles%` albo `%ProgramFiles(x86)%`.
Katalog danych (`AppPaths.DataDir`) to wtedy `%ProgramData%\MaszynyAlert`, a w wersji przenośnej katalog
programu. `AppPaths.UserDataDir` to `%LocalAppData%\MaszynyAlert`. Ścieżki względne z sekcji `paths`
rozwiązuje `AppPaths.Resolve` względem katalogu danych. Przy starcie `AppPaths.EnsureDefaultsCopied` kopiuje
`config.example.json` i `rules.example.json` do `config.json` i `rules.json`, jeśli ich brak.

| Plik / katalog | Format | Kto pisze / czyta |
|---|---|---|
| `config.json` | JSON z komentarzami `//` i przecinkami końcowymi, pola camelCase | `ConfigLoader.LoadAppConfig`, `Validate`, `SaveAppConfig` |
| `rules.json` | JSON (`RuleSet`: `codes`, `databases`, `rules`) | `ConfigLoader.LoadRules`, `MessageMapper` |
| `secrets.dat` (+ `secrets.lock`) | JSON: klucz → Base64 z `ProtectedData.Protect` | `DpapiSecretStore` |
| `history.csv` | CSV, separator `;`, UTF-8 z BOM, nagłówek `czas;maszyna;zdarzenie;opis;kategoria;pelny_tekst;komputer;uzytkownik` | `HistoryWriter` |
| `downtime.jsonl` | JSON Lines, wpisy `DowntimeEpisode` kluczowane `EpisodeId` | `DowntimeJournal` |
| `alarm-instructions.json` | JSON z wersją i słownikiem wpisów (treść, czas i autor edycji, znacznik usunięcia) | `AlarmInstructionStore` przez `AlarmHelpService` |
| `group-instructions/group-instructions.json` | jak wyżej, zbiór wspólny grupy | `SharedInstructions` |
| `group-settings/` | `journal.json`, `generation-<id>.json`, `settings.lock` | `GroupSettingsStore` (otwierany przez `GroupSettingsClient.OpenStore`) |
| `shared-config/shared-config.json` | JSON `SharedConfigDocument`; zapis przez `.tmp` z kopią `.bak` pod plikiem-zamkiem `.lock` | `SharedConfigStore`, `SharedConfigSync` |
| `state/` | `observation.jsonl`, znacznik `stop.requested`, `updates/` (pobrane instalatory) | `ObservationJournal`, `WatchdogTask`, `UpdateCoordinator` |
| `updates-admin/` | wynik i log zadania aktualizacji | `UpdateTaskVerifier`, `installer/Update-Task.ps1` |
| `logs/` | `app-<data>.log` (Serilog, rotacja dzienna, 14 plików) | `Program.BuildHost` |
| `captures/` | PNG zrzutów | `FileCaptureStore`; porządkowanie wg `captureRetentionDays` i `capturesMaxMegabytes` w `MonitorSupervisor` |
| `%LocalAppData%\MaszynyAlert\overlay.json`, `theme.json` | JSON | `OverlaySettingsStore`, `ThemeSettingsStore` |

Kolejność warstw konfiguracji przy starcie (`Program.RunInstance`): najpierw `config.json` i `rules.json`;
jeśli `GroupSettingsStore.ReadForStartup` zwraca zatwierdzoną generację, `GroupSettingsData.ApplyTo` zastępuje
`machines`, `profiles`, `timing`, `ocr` i `cluster`, a reguły pochodzą z profilu grupy; na koniec
`SharedConfigDocument.ApplyTo` nakłada wspólną konfigurację. Warstwa wspólna, która nie przechodzi
`ConfigLoader.Validate`, jest pomijana z ostrzeżeniem w logu. Uszkodzone ustawienia grupy wyłączają klaster
i program działa na konfiguracji lokalnej.

Magazyn sekretów (`SecretKeys`, `AdminPassword`, `GroupSettingsStore`) przechowuje: hasła VNC `vnc:<machineId>`,
klucz `cluster:hmac`, klucze generacji grupy `cluster:generation:<id>`, sekret tokenu katalogu wydań
`updates:access-client-secret` i hash hasła Panelu `admin-password-hash`.

Odcisk profilu grupy (`GroupFileCodec.ProfileHash`) to SHA-256 (zapis szesnastkowy małymi literami)
kanonicznego JSON-a obiektu `GroupProfile` z wyczyszczonym słownikiem `Instructions`. Do odcisku wchodzą więc
`machines`, `profiles`, `timing`, `ocr` i `rules`; instrukcje naprawy nie. Zapis kanoniczny sortuje klucze
obiektów porządkowo na każdym poziomie i zachowuje kolejność tablic. `ProfileHashSchema` zawiera zamrożoną listę
pól `Fields0479` (np. `MachineConfig.host`, `ScreenProfile.statusBarRoi`, `Rule.pattern`,
`TimingConfig.pollIntervalMs`); pole spoza tej listy jest pomijane tylko wtedy, gdy jest zarejestrowane
w `AddedAfter0479` i ma wartość domyślną. Słownik `AddedAfter0479` jest obecnie pusty. Klucze słowników
(np. identyfikatory profili ekranu) nigdy nie są pomijane. `GroupProfile` z założenia nie zawiera ścieżek,
preferencji interfejsu, globalnego wyłącznika ani sekretów.

## 6. Praca wielostanowiskowa

Role komputera (`PeerRole`): `Watcher` (obserwator — utrzymuje połączenia VNC i rozgłasza stan) oraz `Ui`
(komputer tylko wyświetla alerty odebrane z sieci). `MonitorSupervisor` („nadzorca maszyn” w komentarzach kodu)
nie jest osobną rolą klastra: `AppOrchestrator` uruchamia go tylko wtedy, gdy `ClusterRuntime.IsWatcher`.
Przy `cluster.enabled = false` węzeł jest obserwatorem na stałe, a magistrala UDP nie jest uruchamiana.

Transport (`UdpPeerBus`): rozgłoszenie UDP na porcie `udpPort` (domyślnie 47777) na adresy rozgłoszeniowe
lokalnych interfejsów, opcjonalnie także na adresy z listy `peers`. Każdy datagram jest podpisany HMAC-SHA256
(`HmacSigner`, klucz co najmniej 16 bajtów, generowany jako 32 bajty), a `MessageCodec.TryDecode` sprawdza
podpis i znacznik czasu z tolerancją `clockSkewToleranceSec`. Datagram ma limit 60 000 bajtów; zbyt duży
`StateMessage` jest przycinany. Kluczem jest klucz zatwierdzonej generacji grupy albo, bez grupy, sekret `cluster:hmac`.

Wybór obserwatora: każdy komputer wysyła `HeartbeatMessage` co `heartbeatIntervalMs`; komputer milczący dłużej
niż `peerTimeoutMs` uznaje się za nieobecny. `Election.ChooseWatcher` wybiera najniższy `priority`, a przy remisie
pierwszą nazwę hosta według `StringComparer.Ordinal`. `WatcherRoleController` przejmuje rolę dopiero po `takeoverDelayMs`
nieprzerwanej kandydatury i oddaje ją natychmiast, gdy pojawi się lepszy kandydat albo wykryje uśpienie
(skok zegara monotonicznego ponad `sleepJumpThresholdMs` lub `PowerModeChanged.Resume`). Obserwator wysyła
`StateMessage` co `stateIntervalMs` z aktywnymi alertami, zamkniętymi epizodami i migawkami maszyn.

Warstwa wspólna replikuje się wiadomościami `SharedConfigMessage`, `InstructionChangeMessage`,
`InstructionsSyncRequest`/`Reply`, `DowntimeSyncRequest`/`Reply` i `ObservationSyncRequest`/`Reply`.
Heartbeat niesie odciski `SharedConfigHash` i `InstructionsHash`, po których komputery wykrywają rozbieżność.
Konflikt dokumentów wspólnej konfiguracji rozstrzyga `SharedConfigDocument.Wins`: najpierw późniejszy `EditedAt`,
potem `EditedBy` (porównanie `string.CompareOrdinal`). Dokument zawiera `timing`, `overlay`, przełączniki maszyn (`machines`)
i zgodę na aktualizację (`updateApproval`). Postoje uzupełniane są wstecz najwyżej o `downtimeSyncWindowDays`
dni, a ponowne pytanie tego samego komputera następuje po `downtimeSyncIntervalMs`. Potwierdzenia idą jako
`AckMessage` i są powtarzane w polu `RecentAcks` heartbeatu; `AckMemory` pamięta je przez `ackMemoryHours`.

Grupa komputerów: przy sekcji `cluster.group` `ClusterRuntime` tworzy `GroupClusterNode` (protokół w wersji 2,
`GroupIdentity.CurrentProtocolVersion`), bez niej `ClusterNode`. `GroupIdentity.Compare` rozróżnia stany
`Compatible`, `OtherGroup`, `OtherProtocol`, `OtherKey` i `OtherProfile`; ten ostatni oznacza inną rewizję lub inny
odcisk profilu. Ustawienia grupy zapisuje pomocnik z UAC (`--group-settings-helper`, `GroupSettingsClient`):
sekrety przechodzą przez nazwany potok z listą ACL ograniczoną do SID użytkownika, nigdy przez wiersz poleceń.
`GroupSettingsStore` zapisuje generację etapami `Stage`, `Commit` i `Abort` pod dziennikiem `journal.json`.
`GroupFirewall` dodaje wyłącznie własną regułę przychodzącego UDP `MaszynyAlert.Group.Udp`, bez profilu
publicznego i bez portów VNC. Zmianę profilu sieci wykonuje `NetworkProfileHelper`
(`--set-private-network-profile`).

Zgoda na aktualizację jest jedna dla grupy: `UpdateCoordinator.ApproveAndInstallAsync` zapisuje `UpdateApproval`
(wersja, plik, komputer, czas) w dokumencie wspólnej konfiguracji przez `SharedConfigSync.Approve` i rozgłasza
go. Pozostałe komputery odbierają zgodę zdarzeniem `SharedConfigSync.Changed`.

## 7. Aktualizacje

`HttpUpdateSource` czyta `INDEX.md` z katalogu `updates.url` (limit 1 MiB, czas 15 s). `ReleaseCatalog.ParseIndex`
przyjmuje wiersze tabeli Markdown z nazwą pliku pasującą do `(MaszynyAlert|VncAlert|VNC-Alert)-Setup-<wersja>.exe`,
rozmiarem `(<n> B)` i 64-znakową sumą SHA-256; najnowsze wydanie to wpis o najwyższej wersji. Klient HTTP
nie podąża za przekierowaniami (`AllowAutoRedirect = false`); przekierowanie albo kod 401/403 oznacza wymóg
logowania. Przy ustawionym `accessClientId` i sekrecie żądania niosą nagłówki `CF-Access-Client-Id`
i `CF-Access-Client-Secret`. Pobieranie zapisuje plik `<nazwa>.part` i nadaje mu właściwą nazwę dopiero po zgodności
rozmiaru i SHA-256 ze spisem.

`UpdateCoordinator` działa tylko w wersji zainstalowanej i przy `updates.enabled`. Pierwsze sprawdzenie
następuje po `StartupDelay` (2 min), ocena zgody i alarmu co `TickInterval` (30 s), a odczyt katalogu co
`updates.checkIntervalMinutes`. Bez zgody pokazuje dostępną wersję (`UpdatePhase.Available`); „Później” chowa
okno na 24 h. Ze zgodą z grupy i `autoInstallWhenApproved = true` pobiera instalator i uruchamia go dopiero
wtedy, gdy `CanInstallNow` jest prawdziwe: `UpdateService` sprawdza, że wśród widocznych alertów nie ma
niezamkniętego alertu rodzaju `Error`. Do tego czasu stan to `ReadyToInstall`. Zgoda kliknięta na danym
komputerze instaluje od razu, bez względu na alarm. Przy `autoInstallWhenApproved = false` komputer czeka na
przycisk „Zainstaluj” (`WaitingForAdmin`).

`WindowsUpdateInstaller` ponownie sprawdza SHA-256 pliku, zapisuje skrypt `install-update.ps1` w katalogu
`state\updates` i uruchamia go w Windows PowerShell. Skrypt ma dwa etapy: pierwszy uruchamia drugi poza drzewem
procesów programu. Gdy zadanie Harmonogramu „VNC Alert update” jest zarejestrowane, program zapisuje
`update-request.json`, a skrypt wyzwala zadanie (`schtasks /run`) i czeka na jego wynik — bez UAC. Bez zadania
skrypt uruchamia instalator przez UAC z `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`. Potem uruchamia program na
koncie operatora i potwierdza jego start muteksem `Global\MaszynyAlert.App`. Kody wyniku: 0 zainstalowano,
3 UAC anulowane, 4 błąd instalatora, 5 inny błąd, 6 zadanie odrzuciło plik.

Zadanie „VNC Alert update” (`installer/Register-UpdateTask.ps1`) działa jako SYSTEM (`S-1-5-18`) z najwyższymi
uprawnieniami, nie ma wyzwalaczy, `MultipleInstancesPolicy` to `IgnoreNew`, a grupa Users ma tylko prawo
odczytu i uruchomienia (`D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;BU)`). Skrypt `Update-Task.ps1` woła
`MaszynyAlert.App.exe --update-task-verify` (`UpdateTaskCommand`, `UpdateTaskVerifier`): program jako SYSTEM
sprawdza żądanie, kopiuje plik do katalogu `updates-admin` dostępnego tylko dla administratorów i SYSTEM,
porównuje rozmiar i SHA-256 kopii ze spisem i przyjmuje wyłącznie wersję nowszą od zainstalowanej.
Instalator uruchamia następnie skrypt zadania, a nie program.

Zadanie „VNC Alert watchdog” (`installer/Register-Watchdog.ps1`) co minutę i przy logowaniu uruchamia
`MaszynyAlert.App.exe --if-not-running` z uprawnieniami grupy Users (`S-1-5-32-545`, `LeastPrivilege`).
Komenda `--stop` zostawia znacznik `stop.requested`, po którym watchdog nie wznawia programu przez 10 minut
(`WatchdogTask.SuppressionWindow`).

Instalator Inno Setup (`installer/MaszynyAlert.iss`) wymaga uprawnień administratora, systemu Windows
w wersji co najmniej 10.0.22000 i instaluje do `{autopf}\VNC Alert`; `PrepareToInstall` dopuszcza wyłącznie
katalog `Program Files\VNC Alert` albo `Program Files\MaszynyAlert`. Dane leżą w `{commonappdata}\MaszynyAlert`
i nie są nadpisywane: sekcja `[Files]` wyklucza z paczki m.in. `config.json`, `rules.json`, `secrets.dat`,
`history.csv`, `downtime.jsonl`, `alarm-instructions.json*`, `logs`, `captures`, `state`, a dźwięki kopiuje
z flagą `onlyifdoesntexist`. Po instalacji `Initialize-Data.ps1` tworzy brakujące pliki i ustawia listy ACL
(Users z prawem zapisu do `logs`, `captures`, `test-ekran`, `state`, `group-instructions`, `shared-config`,
`history.csv` i `downtime.jsonl`; `updates-admin` tylko dla administratorów i SYSTEM). Instalator dopisuje katalog
programu do `PATH`, rejestruje watchdog i zadanie aktualizacji, a przy pierwszej instalacji dodaje wpis
autostartu `MaszynyAlert` w `HKLM\...\Run`. Działający program zatrzymuje komendą `--stop`. Instalacja wersji
starszej od zainstalowanej (porównanie `FileVersion` w `PrepareToInstall`) jest blokowana komunikatem.

## 8. Bezpieczeństwo

- DPAPI: `DpapiSecretStore.ForMachine` (wersja zainstalowana, `DataProtectionScope.LocalMachine`,
  `%ProgramData%\MaszynyAlert\secrets.dat`) albo `ForCurrentUser` (wersja przenośna, `CurrentUser`,
  `%LocalAppData%\MaszynyAlert\secrets.dat`), z entropią SHA-256 tekstu `MaszynyAlert.v1`.
  Zapis odbywa się pod międzyprocesową blokadą pliku `secrets.lock`.
- Hasło Panelu administracyjnego: `AdminPassword.Hash` wymaga co najmniej 10 znaków i liczy PBKDF2-HMAC-SHA256
  z losową solą 16 bajtów, 600 000 iteracji i wynikiem 32 bajtów, zapisanym jako `Base64(sól).Base64(hash)`
  w magazynie sekretów pod kluczem `admin-password-hash`. `AdminLoginWindow` weryfikuje hasło wyłącznie
  wobec tego zapisanego hasha (`AdminPassword.VerifyLogin`, porównanie `CryptographicOperations.FixedTimeEquals`).
  Brak zapisanego hasha oznacza brak dostępu do Panelu do czasu ustawienia hasła w oknie Ustawień. Po błędnym
  haśle kolejna próba jest możliwa po 3 sekundach (`AdminLoginThrottle`). Zmiana hasła wymaga podania obecnego.
- Katalog wydań: `ConfigLoader.ValidateUpdates` przyjmuje `updates.url` tylko jako pełny adres `https://`
  (wyjątek: `http://` dla `localhost` i `127.0.0.1`) i odrzuca adres z nazwą użytkownika lub hasłem.
  `checkIntervalMinutes` musi mieścić się w zakresie 5–1440.
- Brak wysyłania wejścia do maszyn: klient RFB nie ma komunikatów klawiatury, myszy ani schowka (sekcja 4),
  a `AlarmHelp` zgodnie z opisem klasy nie zmienia parametrów sterownika i nie zleca zdalnego resetu.
- Klaster: datagramy z błędnym podpisem HMAC albo znacznikiem czasu poza tolerancją są odrzucane;
  `GroupIdentity` nie zawiera klucza sieciowego.
- Zwykły start programu na koncie SYSTEM kończy się kodem 3 bez uruchamiania monitoringu (`Program.IsLocalSystem`).

## 9. Budowanie, testy i wydania

Wersja (`Directory.Build.props`): `VncAlertMajorMinor` = `0.4`, `VncAlertBuildNumberOffset` = `270`.
Gdy ustawiona jest zmienna `GITHUB_RUN_NUMBER`, numer kompilacji to `270 + GITHUB_RUN_NUMBER` i wersja ma postać
`0.4.<270 + numer przebiegu>` (sufiks z `VNCALERT_VERSION_SUFFIX`). Lokalnie, bez numeru przebiegu, wersja
to `0.4.0-dev`.

Testy:

- `testy.ps1` — `dotnet restore`, `build -c Release` i `test` całego `src/MaszynyAlert.sln`; wyniki TRX
  w `TestResults/Windows`.
- `testy.sh` — `dotnet test -c Release` dla `MaszynyAlert.Core.Tests` i `MaszynyAlert.Rfb.Tests` (projekty
  bez zależności od Windows).

Workflow `ci.yml` (pull request, push na `main`, ręczne uruchomienie) wywołuje workflow wielokrotnego użytku
`mbuasm/serwer-laptop/.github/workflows/testy.yml@main` z `typ: desktop` i uprawnieniem `contents: read`.

Workflow `wydanie.yml`:

- job `sprawdz` wywołuje `ui-testy.yml`, który uruchamia `testy-ui.ps1` (paczka, smoke startu, instalator,
  test instalacji, testy `MaszynyAlert.Windows.Tests` z wynikiem trx) — dla pull requestów poza szkicami
  i przy ręcznym uruchomieniu;
  runner wskazuje wejście `runs_on`, a gdy jest puste — zmienna `SPRAWDZ_RUNS_ON` albo `windows-latest`;
- job `wydanie` (tylko ręczne uruchomienie na `refs/heads/main`, po `sprawdz`) wywołuje wspólny `wydanie.yml`
  z `projekt: maszyny-alert`, poleceniem budowania `scripts\Przygotuj-Wydanie.ps1` i instalatorem
  `installer/MaszynyAlert-wydanie.iss`. Domyślny adres katalogu wydań w programie to
  `https://wydania.mbuas.pl/maszyny-alert/` (`UpdatesConfig.Url`).

Skrypty w `scripts`:

- `Przygotuj-Wydanie.ps1` — wymaga `GITHUB_RUN_NUMBER` i `GITHUB_SHA`, w razie potrzeby instaluje SDK z `global.json`,
  wykonuje `dotnet publish` (`win-x64`, self-contained) do `publish/app`, dokłada `config.json`, `rules.json`,
  dźwięki i `test-ekran`, sprawdza komplet plików i zapisuje `build-info.json` (wersja, commit, przebieg).
- `installer/MaszynyAlert-wydanie.iss` — ustawia `PublishDir` na `publish\app` i nazwę wyjściową
  `VncAlert-Setup-<FileVersion>`, resztę dołącza z `MaszynyAlert.iss`.
- `Build-Installer.ps1` — szuka kompilatora Inno Setup 6 (`-CompilerPath`, `INNO_SETUP`, domyślne katalogi, `PATH`),
  sprawdza paczkę, opcjonalnie podpisuje pliki (`Sign-Files.ps1`, sekret `CODESIGN_PFX_BASE64`) i zapisuje
  `VncAlert-Setup.exe` z plikiem `SHA256SUMS.txt`.
- `Test-PortablePackage.ps1` — sprawdza komplet plików paczki, zgodność `build-info.json` z wersją EXE/DLL,
  numer wersji względem przebiegu CI, manifest, test startu (`--smoke-test`), edytor ustawień, komendę
  `vncalert` i ciszę watchdoga po komendzie zatrzymania.
- `Test-Installer.ps1` — przeznaczony wyłącznie dla jednorazowego runnera (zmienia Program Files i HKLM);
  sprawdza instalację i aktualizację z wersji bazowej, DPAPI i ACL, blokadę starszej wersji, autostart, `PATH`,
  watchdog, zadanie aktualizacji (w tym odmowy) i zachowanie danych. Wersję bazową i `InstallerProbe`
  przygotowuje `Prepare-InstallerTests.ps1`.

## 10. Narzędzia pomocnicze

`probe.exe` (`src/MaszynyAlert.Probe`, komendy w `Probe/Commands`):

| Komenda | Klasa | Działanie |
|---|---|---|
| `auto` | `AutoCommand` | rozpoznanie maszyn, test OCR, obserwacja do Ctrl+C, nasłuch testu sieci, archiwum ZIP folderu `out` |
| `diagnoza` | `DiagnozaCommand` | diagnostyka wszystkich maszyn naraz; tryb `szybki` z podsumowaniem albo `dlugi` (kolektor) |
| `haslo` | `HasloCommand` | zapis, usuwanie i lista haseł VNC kolektora (DPAPI bieżącego konta) |
| `info` | `InfoCommand` | wersja RFB, typ uwierzytelnienia, rozmiar ekranu, format piksela i zrzut całego ekranu |
| `shared-test` | `SharedTestCommand` | test drugiego klienta obok istniejącego podglądu |
| `ocr` | `OcrCommand` | dostępność OCR Windows, odczyt zrzutów, wyznaczenie obszarów postępu i nazwy pliku |
| `watch` | `WatchCommand` | obserwacja w pętli z zapisem zrzutów przy zmianach |
| `bus-test` | `BusTestCommand` | test widoczności komputerów w sieci UDP |
| `capture` | `CaptureCommand` | pojedynczy zrzut jednej maszyny |
| `help` | `HelpCommand` | lista komend |

Opcje globalne obejmują m.in. `--out`, `--password`, `--username`, `--no-auth-prompt`, `--config`, `--machines`
i `--all`. Komendy łączące się z maszynami zwracają 0, gdy połączono wszystkie wybrane maszyny, 3 — część,
4 — żadnej, 1 — błąd.

`tools/diagnoza-hali` — skrypty PowerShell 5.1 wokół `probe.exe`. `Diagnoza-Hali.cmd` uruchamia
`Diagnoza-Hali.ps1`, który w trybie `szybki` zbiera informacje o środowisku i sieci, wykonuje
`probe.exe diagnoza`, pokazuje podsumowanie i pakuje wyniki do ZIP. Kolektor długi obsługują
`Kolektor-Start.ps1` (potwierdzenie startu), `Zatrzymaj-Kolektor.ps1` (zatrzymanie) i `Zbierz-Wyniki.ps1`
(zamknięcie, spakowanie wyników i wznowienie zbierania). `Zbuduj-Pakiet.ps1` publikuje `probe.exe`
(`win-x64`, self-contained) do paczki, a pliki `Test-*.ps1` testują te skrypty.

`vncalert.cmd` — komenda instalowana w katalogu programu, dostępna w `PATH`. `vncalert killapp` wywołuje
`MaszynyAlert.App.exe --stop` (`StopRequest`): program zamyka się normalnie, a komenda zwraca 0, gdy
zatrzymanie się powiodło w ciągu 10 sekund. Wywołanie bez argumentu wypisuje listę komend i zwraca 2.
