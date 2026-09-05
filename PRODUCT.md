# Product

## Register

product

## Users

Jeden użytkownik (Wojciech), własny uczeń klasy 2p. Otwiera `dashboard.html`
lokalnie w przeglądarce (offline, bez serwera) żeby szybko sprawdzić oceny,
frekwencję, plan lekcji i sprawdziany — zwykle wieczorem albo rano przed
szkołą, czasem w pośpiechu. To narzędzie do jednego zadania: odczytać dane
dziennika szybciej i przyjemniej niż w oficjalnej appce eduVulcan.

## Product Purpose

Jednoplikowy, samowystarczalny dashboard generowany przez `export.py` /
`vulcanscope export` z danych Hebe API. Sukces = dane widać natychmiast,
bez czekania na sieć czy serwer, i nawigacja między widokami (Przegląd/Oceny/
Frekwencja/Plan/Sprawdziany/Zadania/Uwagi/Wiadomości) jest oczywista i szybka.

## Brand Personality

Precyzyjny, spokojny, techniczny-ale-ciepły. Trzy słowa: **precyzja, spokój,
rzemiosło**. Inspiracje podane wprost przez użytkownika:

- **shelter.pl** — za responsywność i przyjemność nawigacji: siatka trzyma się
  kupy na każdej szerokości, dużo oddechu, jeden zdecydowany akcent, brak
  szumu wizualnego.
- **axolotgames.com** — za klimat (mono/terminalowy, czarno-biały, pixel-grid
  logo, nawiasowe indeksy `[01]`) i za odwagę dodania fizyki/ruchu, którego
  nikt nie kazał robić, bo po prostu podnosi jakość. Wniosek: dbałość
  o szczegół i gotowość dodać coś ponad brief, jeśli service'uje odczyt danych.

## Anti-references

Obecny wygląd dashboardu (przed tą zmianą) — user: "wygląda jak ai slop".
Konkretnie do wywalenia: fioletowo-różowy gradient jako domyślny akcent,
glassmorphism na każdym panelu (`backdrop-filter: blur` wszędzie), dryfujące
aura-blob-y w tle, kafelki hero-metric (ikona + wielka liczba + etykieta —
sztampa SaaS), miękkie cienie na wszystkim, font systemowy bez charakteru.

## Design Principles

1. **Dane czytelne ponad ozdobę** — kontrast i hierarchia typograficzna niosą
   znaczenie, nie kolor tła panelu.
2. **Jeden akcent, nie tęcza** — paleta ocen (czerwień→zieleń) zostaje jako
   jedyny miejsce z wieloma kolorami na raz, bo tam kolor niesie informację.
3. **Fizyczność ma służyć, nie rozpraszać** — sprężyste easing i lekka
   reakcja na kursor tam, gdzie to naturalne (hover na ocenie, karcie), zero
   ruchu na danych, które trzeba czytać w bezruchu.
4. **Rzemiosło widoczne z bliska** — detale (numeracja sekcji, mono do liczb,
   krawędzie zamiast rozmycia) budują wrażenie "ktoś to zrobił z uwagą", nie
   "wygenerowane".
5. **Offline-first zostaje święte** — zero CDN, zero webfontów, jeden plik.

## Accessibility & Inclusion

WCAG AA (kontrast tekstu ≥4.5:1, dużego tekstu ≥3:1). `prefers-reduced-motion`
wyłącza dryf/parallax i sprężyste animacje (crossfade zamiast). Nawigacja
klawiaturą działa już przez natywne `<button>` — zachować przy przebudowie.
