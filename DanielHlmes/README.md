# Daniel Holmes Trading Bot (v1.0.0)

A **DanielHlmes** egy professzionális, cTrader platformra írt algoritmikus kereskedési robot (cBot), amely kifejezetten az **Arany (XAUUSD) 15 perces (M15)** grafikonján való kereskedésre lett optimalizálva.

A robot ötvözi a klasszikus árfolyam-kitörési (Breakout) stratégiákat a hamis kitörések (Fakeout/Fade) elleni védelmi és ellenirányú rendszerekkel, kiegészítve szigorú tőkevédelmi és FTMO-kompatibilis kockázatkezelési modulokkal.

---

## 📊 Backtest Eredmények (1 Éves Futás: 2025. június – 2026. június)

Az optimalizált alapértelmezett paraméterekkel futtatott 1 éves teszt kiemelkedő eredményeket produkált:

| Metrika | Érték |
| :--- | :--- |
| **Kezdő tőke** | $25,000.00 USD |
| **Nettó Profit** | **+$30,914.98 USD (+123.66%)** |
| **Profit Factor (Nyereségtényező)** | **2.17** (Longs: 2.53, Shorts: 1.72) |
| **Max. Tőke Drawdown (Equity Drawdown)** | **12.43%** |
| **Max. Egyenleg Drawdown (Balance Drawdown)**| **8.49%** |
| **Összes kötés** | 99 (60 Long, 39 Short) |
| **Nyerő arány (Win Rate)** | **33.3%** |
| **Átlagos profit kötésenként** | **+$312.27 USD** |
| **Annualizált Sharpe-mutató (Sharpe Ratio)** | **2.78** (Kiemelkedően magas robusztusság) |

### Kötések Megoszlása:
- **Long (Buy) kitörések**: +$22,603.24 nyereség (36.7% Win Rate a magas 1:5 R:R miatt rendkívül nyereséges).
- **Short (Sell) kitörések**: +$8,311.74 nyereség (28.2% Win Rate, a H4 EMA szűrő megvédte a botot az emelkedő piacon a felesleges shortolásoktól).

---

## ⚙️ Kereskedési Stratégia (Modulok)

### 1. Setup A – Consolidation Breakout (Sávkitörés)
A stratégia a délutáni/esti amerikai kereskedési időszak (15:00 - 22:00 CET) gyertyáiból egy Support & Resistance (S&R) sávot épít fel a gyertya-testek legmagasabb és legalacsonyabb szintjei alapján.
- **Kitörés észlelése**: Ha 22:00 CET után a gyertya a sávon kívül zár.
- **Szűrők**:
  - **Doji szűrő**: Kizárja a bizonytalanságot jelző gyertyákat (Test/Terjedelem arány < 0.4).
  - **Wick (Kanóc) szűrő**: Kizárja a kanóc nélküli gyertyákat (wick fill risk), minimalizálva a hirtelen visszafordulásokat.
  - **EMA 200 (M15)** szűrő a lokális trend követésére.
  - **EMA 200 (H4)** felsőbb idősíkú szűrő a globális trend irányába való kötéshez (bika piacon nem shortol).
- **Célár**: Szigorú **1:5 Risk-to-Reward (R:R)** arány (pl. 30 pip SL / 150 pip TP).

### 2. Setup C – Breakout Fade (Fakeout Rejection)
Ez a modul a Setup A kitörések sikertelenségét (hamis kitörés) használja ki.
- **Belépés**: Ha a kitörés megerősödése után az ár még a Stop megbízás teljesülése előtt **visszazár a sávba**.
- **Művelet**: A bot **törli a függőben lévő Setup A megbízást**, és azonnal **piaci ellenirányú megbízást nyit** (hamis bika kitörésre Sell, hamis medve kitörésre Buy).
- **Stop Loss**: A kitörési kísérlet csúcsánál / aljánál (szűk SL).
- **Take Profit**: A sáv ellentétes oldala (vagy minimum 1:2 R:R).

---

## 🛡️ Kockázatkezelés és Tőkevédelem

- **Kockázat kötésenként**: Alapértelmezetten **1%** az aktuális számlaegyenlegre számolva.
- **FTMO Drawdown szűrők**:
  - **Napi Drawdown korlát (5.0%)**: Ha a napi equity 5%-ot esik a napi csúcshoz képest, a bot leáll.
  - **Teljes Drawdown korlát (10.0%)**: Ha a számla equity 10%-ot esik a kezdeti egyenleghez kpest, a bot leáll.
- **Heti veszteség limit (3.0%)**: Ha a heti equity drawdown eléri a 3%-ot a heti csúcsértékhez képest, a bot szünetelteti a kereskedést a következő hétfőig.
- **Vesztő sorozat Cooldown (5 nap)**: Ha 5 egymást követő veszteséges kötés történik, a bot **5 napos teljes kereskedési szünetet** tart.

---

## 🔧 Paraméterek Beállításai (Default)

A kódban rögzített optimalizált alapértelmezett beállítások:

```csharp
// Setup Selection
EnableSetupA = true;
EnableSetupB = false;      // Pullback modul alapértelmezetten ki van kapcsolva
EnableSetupC = true;       // Breakout Fade modul bekapcsolva

// Session Range Setup
SessionStartHour = 15;     // Amerikai session kezdete (CET)
SessionEndHour = 22;       // Amerikai session vége (CET)
RangeLookbackBars = 40;    // Range mélységi vizsgálat

// Setup A (Breakout)
RewardRiskRatio = 5.0;     // 1:5 Risk-to-Reward arány
UseEmaFilter = true;       // M15 EMA 200 szűrő aktív

// Setup C (Fade)
SetupCRewardRiskRatio = 2.0; // 1:2 R:R Fade kötéseknél

// Risk Filters & FTMO
RiskPercent = 1.0;          // 1% kockázat
MaxWeeklyLossPercent = 3.0; // Heti maximum veszteség limit
MaxConsecutiveLosses = 5;   // 5 veszteség után cooldown
CooldownDays = 5;           // 5 napos cooldown
UseH4EmaFilter = true;      // Globális H4 EMA 200 szűrő aktív
```

---

## 🚀 Telepítés és Használat

1. Telepítsd a **cTrader** platformot.
2. Töltsd le a [DanielHlmes.cs](file:///Users/sebestyenkristof/cAlgo/Sources/Robots/DanielHlmes/DanielHlmes/DanielHlmes.cs) fájlt.
3. A cTrader platform **Algo** fülén kattints a **Robots** szekcióra, majd a **New / Add** gombbal importáld a letöltött fájlt, vagy másold be a C# kódot.
4. Válaszd ki a **XAUUSD** (Arany) szimbólumot **15 perces (m15)** idősíkon.
5. Add hozzá a robotot a grafikonhoz, állítsd be a paramétereket (vagy használd az optimalizált default értékeket), majd kattints a **Start** gombra a kereskedés indításához.

---

## 📌 Felelősségkizárás (Disclaimer)
A robot múltbeli teljesítménye nem garantálja a jövőbeli hozamokat. A tőkeáttételes CFD kereskedés magas kockázattal jár, így a robotot éles számlán való használat előtt javasolt alaposan tesztelni demó környezetben!
