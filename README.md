BlackJack BJH — uruchomienie i zasady

Uruchomienie:
- W folderze Final uruchom plik `BlackJackBJH.exe`.
- Aplikacja nasłuchuje na `http://0.0.0.0:5329`.
- Pierwsze połączenie staje się hostem/adminem. Host widzi przyciski: `Start`, `Nowe zakłady`, `Reset`.

Gra i wypłaty:
- Faza `BETTING`: stawiasz żetony (limit stołu: 2000). Środki są zdejmowane z balansu natychmiast.
- Faza `PLAY`: `Hit`, `Stand`, `Double` (podwaja stawkę, dobiera jedną kartę).
- Blackjack (2 karty = 21): automatyczna wygrana i wypłata 3:2 (netto +1.5× stawki).
- Rozliczenie (`SETTLEMENT`):
  - Wygrana: wypłata `2× stawka` (wraca stawka + wygrana).
  - Remis: wypłata `1× stawka`.
  - Przegrana: `0`.
  - Limity balansu: minimum `0`, maksimum `1 000 000`.

Wynik rundy:
- Okno wyniku pokazuje: wynik (Wygrana/Przegrana/Remis), zysk/strata, stawkę, procent względem stawki oraz nowy balans.

Zasady kolejki i rozłączenia:
- Aktywne miejsce przechodzi automatycznie do następnego gracza.
- Rozłączenie (odświeżenie strony) usuwa gracza ze stołu i nie blokuje kolejki.

Mobilnie:
- Interfejs jest skalowany dla ekranów ≤640px: mniejsze karty, żetony i przyciski są zawijane, aby były widoczne.

Wymagania:
- Windows 64-bit. Przy publikacji użyto trybu `win-x64`, plik EXE jest samodzielny.
