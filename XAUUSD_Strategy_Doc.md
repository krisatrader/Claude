# XAUUSD M15 Breakout & Retest Strategy

## Áttekintés

| Paraméter | Érték |
|-----------|-------|
| Instrument | XAUUSD (Gold/USD) |
| Timeframe | M15 |
| Havi célprofit | ~5% |
| Max SL / trade | 1% account balance |
| Max trades / nap | 1 |
| Risk:Reward | 1:2.5 |
| Session | 07:00–18:00 UTC |

---

## Stratégia logika

### 1. Strukturális szintek meghatározása
- Az EA az utolsó **20 gyertya** swing high/low értékét azonosítja mint kulcs S/R szinteket.
- Breakout akkor érvényes, ha az előző gyertya **legalább 1.5 ponttal** (XAUUSD = $1.5) zár a szint felett/alatt.

### 2. Breakout azonosítás
```
Bullish breakout:  Close[1] > SwingHigh + MinBreakout  AND  H1 EMA trendszűrő bullish
Bearish breakout:  Close[1] < SwingLow  − MinBreakout  AND  H1 EMA trendszűrő bearish
```

### 3. Retest várakozás
- A breakout után az EA **maximum 6 gyertyát** vár.
- A retest zóna: `breakout_level ± (ATR × 0.15)`
- Timeout esetén az állapot resetel, új breakoutra vár.

### 4. Belépési jel (konfirmáció)
| Irány | Feltétel |
|-------|----------|
| Long  | Az előző gyertya visszatért a zónába, ÉS bullish gyertya (Close > Open, test > 30% range) |
| Short | Az előző gyertya visszatért a zónába, ÉS bearish gyertya (Open > Close, test > 30% range) |

### 5. Kockázatkezelés
```
SL_távolság = max(entry − retest_low + ATR×0.25,  ATR×0.5)
TP = SL_távolság × 2.5   →   1:2.5 R:R

Lot méret = (Balance × 1%) / (SL_távolság / tick_size × tick_value)
```

**Break-even win rate** 1:2.5 R:R-nél: **~29%** → konzervatív cél.

---

## Szűrők

### H1 Trend filter (EMA 50)
- Csak a H1 EMA 50 irányában nyit pozíciót.
- Bullish breakout → csak long belépés engedélyezett.
- Bearish breakout → csak short belépés engedélyezett.

### ATR volatilitás filter
- Alacsony volatilitás kiszűrése: `ATR < avgATR × 0.5` → kihagyás.
- Spike szűrés: `ATR > avgATR × 3.0` → kihagyás.

### Session filter
- Csak **07:00–18:00 UTC** között nyit (London + New York session).
- Az ázsiai session kizárva (alacsony volatilitás, fals breakoutok).

---

## Havi 5% profit kalkuláció

| Feltevés | Érték |
|----------|-------|
| Kereskedési napok/hó | 20 |
| Szükséges átlag PnL/nap | +0.25% |
| Win rate (konzervatív) | 50% |
| Átlag nyerő trade | +2.5% (1:2.5 RR × 1% risk) |
| Átlag vesztes trade | −1.0% |
| Várható érték/trade | 0.5×2.5 − 0.5×1 = +0.75% |
| Havi várható | ~10×0.75% = 7.5% (minden napon trades) |

> A stratégia **konzervatív beállításai** (szűrők, max 1 trade/nap) miatt realisztikusan ~3–6 trade/hét aktiválódik → havi 5% elérhető célkitűzés.

---

## Telepítés (MetaTrader 5)

1. Másold a `XAUUSD_Breakout_Retest.mq5` fájlt a `MQL5/Experts/` mappába.
2. Nyisd meg az MT5 MetaEditort → Compile.
3. XAUUSD M15 charthoz húzd az EA-t.
4. Engedélyezd az **Auto-Trading** gombot.
5. Ajánlott broker beállítások:
   - ECN/STP számla (alacsony spread)
   - Min. XAUUSD spread: < 0.3 pip
   - Leverage: 1:100 vagy 1:200

### Ajánlott paraméterek éles kereskedéshez

```
SwingLookback      = 20
MaxRetestCandles   = 6
RetestZonePct      = 0.15
MinBreakoutPips    = 15
RiskRewardRatio    = 2.5
MaxRiskPct         = 1.0
SessionStart       = 7
SessionEnd         = 18
UseTrendFilter     = true
TrendEMAPeriod     = 50
```

---

## Backtesting (Python)

```bash
pip install backtesting pandas numpy yfinance ta
python xauusd_backtest.py
```

MT5 CSV exportból:
```python
data = load_csv_data("XAUUSD_M15_2023.csv")
bt, stats = run_backtest(data, cash=10_000)
```

---

## Figyelmeztetések

- **Demo számlán tesztelj** legalább 3 hónapig éles kereskedés előtt.
- A historikus teljesítmény nem garantálja a jövőbeli eredményeket.
- A XAUUSD piac rendkívül volatilis híresemények (NFP, FOMC, CPI) idején.
- Az EA **nem nyit pozíciót gazdasági hírek előtt/után** (session filterrel részben kezelt).
- Fontold meg egy külső hír-szűrő (News Filter) implementálását.
