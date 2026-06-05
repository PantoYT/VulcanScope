# VulcanScope 🌋

Pełny eksport dziennika **eduVulcan / VULCAN (Hebe API)** + samodzielny, „cool"
dashboard do przeglądania wszystkiego offline.

Pobiera oceny, frekwencję, plan lekcji, sprawdziany, zadania domowe, uwagi i
osiągnięcia — i generuje jeden plik `dashboard.html`, który otwierasz w
przeglądarce (dane są w nim zaszyte, działa bez internetu i bez serwera).

> Projekt korzysta z tego samego mechanizmu logowania co bot **Vred** —
> podpisywanych kluczem RSA żądań do mobilnego API „DzienniczekPlus 3.0".
> Bazuje na reverse-engineeringu [hebece](https://github.com/hypedevss/hebece).

---

## Jak działa logowanie (w skrócie)

To nie jest logowanie hasłem przy każdym żądaniu — to **parowanie urządzenia**:

1. **`register.py`** (jednorazowo) — otwiera przeglądarkę, logujesz się ręcznie na
   eduvulcan.pl. Skrypt pobiera tokeny JWT z `/api/ap`, generuje parę kluczy
   **RSA 2048** + samopodpisany certyfikat X.509 i rejestruje klucz publiczny na
   koncie (`/api/mobile/register/jwt`). Zapisuje `credentials.json`.
2. **`hebe/`** — od tej chwili **klucz prywatny RSA = login**. Każde żądanie jest
   podpisywane (`canonicalUrl + digest + data` → RSA-PKCS#1v1.5-SHA256), a
   nagłówki udają aplikację z Androida. Żadnego hasła, sesji ani wygasania.

`credentials.json` = pełny odczyt dziennika bez hasła → **traktuj jak hasło**
(jest w `.gitignore`).

---

## Co jest eksportowane

| Zasób | Endpoint Hebe | Przykład z eksportu |
|---|---|---|
| Oceny cząstkowe (oba semestry) | `grade/byPupil` | 225 ocen |
| Oceny przewidywane / końcowe | `grade/summary/byPupil` | 49 wpisów |
| Frekwencja + tematy lekcji | `lesson/byPupil` | 1119 lekcji, ~98% |
| Plan lekcji + zmiany/zastępstwa | `schedule/withchanges/byPupil` | 1269 lekcji |
| Sprawdziany i kartkówki | `exam/byPupil` | 79 |
| Zadania domowe | `homework/byPupil` | 3 |
| Uwagi i osiągnięcia | `note/byPupil` | 2 |
| Szczęśliwy numerek | `school/lucky` | — |
| Wiadomości + książka adresowa | `messages/*/byBox`, `addressbook` | 🔒 wymaga eduVulcan **Premium** |

Wszystko ląduje w `data/*.json` (surowe dane) + kompaktowy `data/dashboard_data.json`,
który jest zaszywany w `dashboard.html`.

> **Uwaga o wiadomościach:** API zwraca `EDUVULCAN_PREMIUM` dla skrzynki i książki
> adresowej, bo konto szkoły nie ma aktywnej subskrypcji. Eksport mimo to kończy
> się sukcesem — te dwie sekcje są po prostu pomijane (dashboard pokazuje to
> czytelnie).

---

## Użycie

```powershell
# 1. (jednorazowo) zależności
py -3.12 -m pip install -r requirements.txt
py -3.12 -m playwright install chromium   # tylko dla register.py / weryfikacji

# 2. (jednorazowo) zaloguj się i sparuj urządzenie  →  tworzy credentials.json
py -3.12 register.py
#    ...lub skopiuj istniejący credentials.json z bota Vred

# 3. pobierz dane i wygeneruj dashboard
py -3.12 export.py

# 4. otwórz dashboard.html w przeglądarce  (albo po prostu run.bat)
```

`run.bat` robi krok 3 + 4 jednym kliknięciem.

### Przydatne tryby

```powershell
py -3.12 export.py --render-only      # przebuduj dashboard z ostatnich danych (bez API)
py -3.12 tools/verify_dashboard.py    # headless test: sprawdza brak błędów JS + zrzuty ekranu
```

---

## Dashboard

Jeden samowystarczalny plik HTML w stylu **glassmorphism** (aurora gradient,
wykresy SVG bez żadnych CDN, animowane słupki/donut/wykres liniowy):

- **Przegląd** — średnia ważona, frekwencja, kafelki, ostatnie oceny, wykres średnich, nadchodzące sprawdziany/zadania
- **Oceny** — przełącznik semestrów, średnia per przedmiot, kolorowane „pigułki" ocen (hover = waga, kategoria, nauczyciel, komentarz)
- **Frekwencja** — donut, statystyki, rozbicie per przedmiot, dziennik tematów z wyszukiwarką
- **Plan lekcji** — siatka tygodniowa z nawigacją, zastępstwa (żółte) i odwołane (czerwone)
- **Sprawdziany / Zadania** — grupowane po dacie, odznaki „za N dni"
- **Uwagi** — pozytywne/negatywne karty
- **Motyw** — przełącznik **Aurora (jasny)** / **Midnight (ciemny)**, zapamiętywany w `localStorage`
- **Animacje** — count-up liczników, „rysujące się" słupki, donut i wykres liniowy (średnia w czasie)

> Średnia ważona liczy `+`/`-` jako **+0,5 / −0,25** (najczęstsza konwencja PL —
> stała w `export.py` i `csharp/`, łatwo zmienić). Oceny punktowe i `nb` są pomijane.

---

## C# — CLI + tryb terminalowy

Pełny port w **.NET 10** (`csharp/`) — ten sam mechanizm podpisów RSA, te same dane,
ta sama `dashboard.html`. Dodatkowo interaktywny dashboard w terminalu (Spectre.Console)
oraz komendy z wyjściem `--json` do osadzenia w większej aplikacji.

```powershell
cd csharp
dotnet build
dotnet run -- register            # sparuj konto (bez Pythona) — keygen + register/jwt
dotnet run -- tui                 # interaktywny dashboard w terminalu
dotnet run -- export              # data/*.json + dashboard.html (jak w Pythonie)
dotnet run -- grades -p 2         # oceny semestru 2 (tabela)
dotnet run -- attendance          # frekwencja + statystyki
dotnet run -- plan                # plan lekcji (najbliższe dni)
dotnet run -- exams --all         # sprawdziany
dotnet run -- lucky --json        # {"lucky": null}
```

**Parowanie konta w C# (`register`)** — bez Pythona/Playwright: generuje parę RSA + cert,
loguje Cię w przeglądarce, a po wklejeniu strony `https://eduvulcan.pl/api/ap` (lub
`--ap-file`) robi `register/jwt` + `register/hebe` i zapisuje `credentials.json`.
`register --selftest` weryfikuje kryptografię (roundtrip podpisu) i ścieżkę `register/hebe`.

### Samodzielny `.exe` (bez instalowania .NET)

```powershell
cd csharp
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o dist
# → dist/vulcanscope.exe (~36 MB) — kolega odpala bez żadnego SDK:
.\dist\vulcanscope.exe exams --json
```

**Eksport jako komendy do większej aplikacji:** każda komenda danych przyjmuje `--json`
i wypisuje czysty JSON na stdout (kody wyjścia: `0` ok, `2` błąd API, `3` brak pliku).
Większa aplikacja może wołać np. `vulcanscope grades --json` i parsować wynik — albo użyć
klas `VulcanClient` / `ViewModel` / `Exporter` bezpośrednio jako biblioteki.

> `credentials.json` jest współdzielony z częścią pythonową (auto-wykrywany w górę drzewa).
> Rejestracja konta dalej przez `register.py` (Playwright).

---

## Struktura

```
VulcanScope/
├── hebe/                 # klient Python (signing.py, client.py)
├── export.py             # pobiera wszystko → data/*.json + dashboard.html
├── register.py           # jednorazowe parowanie konta (przeglądarka)
├── web/template.html     # szablon dashboardu (Aurora/Midnight, placeholder na dane)
├── tools/verify_dashboard.py   # headless test (Playwright)
├── csharp/               # port C# (.NET 10)
│   ├── Program.cs
│   └── src/              # Signing, VulcanClient, ViewModel, Exporter, Tui, Cli …
├── run.bat
├── credentials.json      # 🔒 sekret (gitignore)
├── data/                 # 🔒 wyeksportowane dane (gitignore)
└── dashboard.html        # 🔒 wygenerowany (gitignore)
```

Tylko do użytku z **własnym** kontem. Dane zostają lokalnie na Twoim komputerze.
