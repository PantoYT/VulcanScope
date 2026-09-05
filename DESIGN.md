# Design

Wizualny system `dashboard.html` (generowanego przez `export.py` /
`vulcanscope export`). Jeden plik, offline, zero CDN i zero webfontów —
wszystko poniżej musi działać z systemowych fontów i inline CSS/JS.

## Motyw

Domyślny: **ciemny** (`data-theme="midnight"` jako punkt wyjścia, nie
dogrywka). Jasny (`aurora`) zostaje jako przełącznik w localStorage, ale
budowany z tą samą dyscypliną — nie może być "tanią" odwrotnością ciemnego.

## Kolor (OKLCH)

Strategia: **restrained** — stonowana neutralna baza + jeden zdecydowany
akcent. Wielobarwna jest tylko skala ocen, bo tam kolor niesie informację.

```
Ciemny (domyślny):
--bg:        oklch(14% 0.012 260)   /* tło, chłodny prawie-czarny, nie cream */
--surface:   oklch(18% 0.014 260)   /* panele — pełna powierzchnia, bez blur */
--surface-2: oklch(22% 0.016 260)   /* hover / podniesione */
--sunken:    oklch(11% 0.010 260)   /* wgłębienia, tory pasków postępu */
--border:    oklch(30% 0.016 260)
--border-2:  oklch(40% 0.02  260)   /* mocniejsza, hover/focus */
--ink:       oklch(94% 0.01  260)
--ink-muted: oklch(70% 0.015 260)
--ink-faint: oklch(50% 0.015 260)

--accent:       oklch(78% 0.16 75)  /* bursztyn terminalowy */
--accent-hi:    oklch(85% 0.17 75)
--accent-ink:   oklch(16% 0.02  75) /* tekst NA akcencie */

Jasny (aurora):
--bg:        oklch(98% 0.004 260)   /* chłodny prawie-biały, chroma ~0 */
--surface:   oklch(100% 0 0)
--surface-2: oklch(95% 0.006 260)
--border:    oklch(88% 0.01  260)
--ink:       oklch(20% 0.01  260)
--ink-muted: oklch(42% 0.015 260)
--accent:    oklch(58% 0.16 75)     /* ten sam odcień, przyciemniony pod jasne tło */
--accent-ink: oklch(99% 0 0)
```

Skala ocen (bez zmian koncepcji, przeliczona do OKLCH, mniej "cukierkowa"):
`--g6..--g1` od zieleni (oklch 72% 0.17 145) przez żółć/pomarańcz do czerwieni
(oklch 62% 0.19 25). `--info` to osobny chłodny niebieski (oklch 70% 0.13 230)
używany wyłącznie dla "nieobecność usprawiedliwiona" — nigdy jako akcent UI.

## Typografia

Parowanie na osi kontrastu: **mono + humanist sans**, oba systemowe.

```
--font-sans: system-ui, -apple-system, "Segoe UI", Roboto, Helvetica, Arial, sans-serif
--font-mono: ui-monospace, "Cascadia Code", "SF Mono", Consolas, "Roboto Mono", monospace
```

Mono: etykiety nawigacji + indeks `[01]`, duże liczby w Przeglądzie, godziny
w planie, dane tabelaryczne (liczby wyrównane do prawej). Sans: nazwy
przedmiotów, treść uwag, opisy, wszystko co się "czyta" a nie "skanuje".

## Kształt i cień

Ostrzejsze niż poprzednia wersja (był 18px wszędzie — czytało się jako
"generyczny AI bąbel"). `--radius-sm: 6px`, `--radius: 8px`, `--radius-lg: 12px`.
Zero `backdrop-filter`/glassmorphism — panele to pełne powierzchnie z
włosowatą krawędzią (`1px solid var(--border)`), cień tylko przy hover
(`0 4px 16px -8px rgba(0,0,0,.4)`), nigdy ambientowy na spoczynku.

## Ruch

```
--ease-out: cubic-bezier(.16,1,.3,1)     /* domyślne przejścia */
--ease-spring: cubic-bezier(.34,1.56,.64,1)  /* hover na ocenie/karcie — lekki overshoot */
```

Fizyczny akcent (inspiracja axolotgames.com): cienka siatka kropek w tle +
miękki "reflektor" podążający za kursorem (CSS custom properties `--mx/--my`
aktualizowane w `requestAnimationFrame`). Czysto ambientowe, nigdy nie
zasłania danych. `prefers-reduced-motion: reduce` wyłącza reflektor i dryf,
zostaje statyczna siatka + crossfade zamiast innych przejść.

## Komponenty — zmiany koncepcyjne

- **Nawigacja**: bracket-index `[01] Przegląd` (licznik CSS — sekwencja jest
  prawdziwa, to nie jest ozdobny eyebrow). Aktywny stan = pełne wypełnienie
  akcentem, nie gradient.
- **Karty statystyk (Przegląd)**: zdjęty wzorzec hero-metric (ikona-w-rogu +
  wielka liczba + podpis, kafelek z cieniem). Zamiast tego zwarty rząd
  "odczytów": mono-etykieta caps + duża liczba mono + kontekst, oddzielone
  cienką linią — czyta się jak specyfikacja, nie SaaS KPI.
- **Pigułki ocen**: ta sama semantyka kolorów, płaskie wypełnienie zamiast
  glossy-gradientu, hover ze sprężystym `--ease-spring`.
- **Chipy w topbarze**: płaskie, cienka krawędź, bez blur.
