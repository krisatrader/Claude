"""
XAUUSD M15 Breakout & Retest Strategy – VectorBT Backtest (v2)
===============================================================
Futtatás:
    python xauusd_vectorbt_test.py

Függőségek:
    pip install vectorbt pandas numpy

Adatforrás:
    - Elsődleges: CSV export MT5-ből → fájlnév: XAUUSD_M15.csv
      (MT5 → Tools → History Center → Export)
      Várt formátum: <DATE>\t<TIME>\t<OPEN>\t<HIGH>\t<LOW>\t<CLOSE>\t<TICKVOL>
    - Fallback: Szintetikus XAUUSD M15 (2 év, GBM + intraday vol minta)
"""

import numpy as np
import pandas as pd
import vectorbt as vbt
import warnings

warnings.filterwarnings("ignore")
np.random.seed(42)

# ===========================================================================
# PARAMÉTEREK
# ===========================================================================
SWING_LOOKBACK      = 20      # Swing high/low keresési ablak (gyertyák)
MAX_RETEST_CANDLES  = 6       # Max gyertya breakout után retest-re várva
RETEST_ZONE_PCT     = 0.15    # Retest zóna = ATR × ez az érték
MIN_BREAKOUT_PTS    = 1.50    # Min breakout méret ($-ban, XAUUSD)
RISK_REWARD         = 2.5     # TP = SL × RR
ATR_PERIOD          = 14      # ATR periódus
TREND_EMA_PERIOD    = 200     # EMA periódus trend szűrőhöz (M15 × 4 × 50 = H1 EMA50)
MAX_RISK_PCT        = 1.0     # Max kockázat / trade (% of equity)
SESSION_START       = 7       # Session start (UTC óra)
SESSION_END         = 18      # Session end (UTC óra)
INIT_CASH           = 10_000  # Induló tőke ($)
COMMISSION          = 0.25    # Spread/jutalék dolláronként (~ XAUUSD 0.25 pip)


# ===========================================================================
# 1. ADATBETÖLTÉS / GENERÁLÁS
# ===========================================================================

def load_mt5_csv(filepath: str) -> pd.DataFrame:
    """MT5 History Center CSV import."""
    df = pd.read_csv(
        filepath, sep="\t",
        names=["Date", "Time", "Open", "High", "Low", "Close", "Volume"],
        skiprows=1,
    )
    df["Datetime"] = pd.to_datetime(df["Date"] + " " + df["Time"])
    df.set_index("Datetime", inplace=True)
    return df[["Open", "High", "Low", "Close", "Volume"]].astype(float).dropna()


def generate_synthetic_xauusd(years: int = 2) -> pd.DataFrame:
    """
    Szintetikus XAUUSD M15 OHLCV adatsor GBM alapon.
    Csak kereskedési napokat tartalmaz (hétfő–péntek).
    """
    # Kereskedési időpontok generálása (M15, hétfő–péntek, 00:00–23:45)
    all_times = pd.date_range(
        start="2023-01-02 00:00", periods=years * 252 * 96, freq="15min"
    )
    mask = all_times.weekday < 5   # csak hétköznap
    idx  = all_times[mask]
    n    = len(idx)

    # GBM paraméterek (XAUUSD reális)
    start_price  = 1_855.0
    daily_vol    = 0.011       # ~1.1% napi volatilitás
    m15_vol      = daily_vol / np.sqrt(96)
    m15_drift    = 0.00003 / 96

    # Alap log-hozamok
    lr = np.random.normal(m15_drift, m15_vol, n)

    # Intraday volatilitás minta (London + NY session kiemelkedő)
    h = idx.hour
    intra = np.where((h >= 7) & (h < 10), 1.6,          # London nyitó
             np.where((h >= 13) & (h < 16), 1.5,         # NY nyitó
             np.where((h >= 10) & (h < 13), 1.3,         # London–NY átfedés
             np.where((h >= 16) & (h < 18), 1.1,         # NY délután
             0.5))))                                       # Ázsia, éjszaka
    lr *= intra

    # Enyhe momentum effekt
    for i in range(1, n):
        lr[i] += 0.04 * lr[i - 1]

    close = np.round(start_price * np.exp(np.cumsum(lr)), 2)

    # OHLCV
    bar_rng = np.abs(np.random.normal(0, m15_vol * 1.8, n)) * close
    bar_rng = np.clip(bar_rng, close * 0.0003, close * 0.012)

    up_body = np.random.uniform(0.3, 0.85, n)
    high    = close + bar_rng * up_body
    low     = close - bar_rng * (1 - up_body)
    open_   = low + (high - low) * np.random.uniform(0.15, 0.85, n)

    high  = np.maximum(high, np.maximum(open_, close))
    low   = np.minimum(low,  np.minimum(open_, close))
    vol   = np.random.randint(300, 2000, n).astype(float)

    return pd.DataFrame({
        "Open":   np.round(open_, 2),
        "High":   np.round(high,  2),
        "Low":    np.round(low,   2),
        "Close":  close,
        "Volume": vol,
    }, index=idx)


# ===========================================================================
# 2. INDIKÁTOROK
# ===========================================================================

def atr_series(high, low, close, period=14) -> pd.Series:
    tr = pd.concat([
        high - low,
        (high - close.shift(1)).abs(),
        (low  - close.shift(1)).abs(),
    ], axis=1).max(axis=1)
    return tr.ewm(span=period, adjust=False).mean()


# ===========================================================================
# 3. SZIGNÁL GENERÁLÁS
# ===========================================================================

def generate_signals(df: pd.DataFrame) -> dict:
    """
    Végigmegy az adatsoron és visszaadja:
      - long_entries / short_entries (bool Series)
      - sl_ratio / tp_ratio         (float Series, arány entry price-hoz képest)
      - size_usd                    (float Series, pozícióméret USD-ben)

    Megjegyzés: VectorBT sl_stop / tp_stop értéke az entry ártól való
    eltérést várja decimális arányban (pl. 0.01 = 1%).
    """
    n     = len(df)
    close = df["Close"].values
    high  = df["High"].values
    low   = df["Low"].values
    open_ = df["Open"].values
    idx   = df.index

    atr_s  = atr_series(df["High"], df["Low"], df["Close"], ATR_PERIOD)
    ema_s  = df["Close"].ewm(span=TREND_EMA_PERIOD, adjust=False).mean()
    sh_s   = df["High"].shift(2).rolling(SWING_LOOKBACK).max()
    sl_s   = df["Low"].shift(2).rolling(SWING_LOOKBACK).min()

    atr_v  = atr_s.values
    ema_v  = ema_s.values
    sh_v   = sh_s.values
    slo_v  = sl_s.values
    hours  = idx.hour

    # Kimeneti tömbök
    long_e   = np.zeros(n, dtype=bool)
    short_e  = np.zeros(n, dtype=bool)
    sl_ratio = np.full(n, np.nan)   # SL távolság / entry_price  (pozitív)
    tp_ratio = np.full(n, np.nan)   # TP távolság / entry_price  (pozitív)
    size_usd = np.zeros(n)          # Pozícióméret USD-ben

    # Állapotgép
    bo_dir     = 0
    bo_level   = 0.0
    rt_count   = 0
    waiting    = False
    bo_atr     = 0.0
    last_day   = None
    in_trade   = False
    trade_end  = -1

    for i in range(SWING_LOOKBACK + 5, n - 1):

        # Pozíció lezárásának szimulációja (VectorBT kezeli, de az 1-trade/nap
        # logikához szükség van arra, hogy tudjuk, "befejeztes-e" a trade)
        if in_trade and i > trade_end:
            in_trade = False

        curr_day = idx[i].date()
        if last_day == curr_day:
            continue
        if in_trade:
            continue
        if not (SESSION_START <= int(hours[i]) < SESSION_END):
            continue

        cur_atr = atr_v[i]
        swing_h = sh_v[i]
        swing_l = slo_v[i]

        if np.isnan(cur_atr) or np.isnan(swing_h) or np.isnan(swing_l):
            continue
        if cur_atr <= 0:
            continue

        p_close = close[i - 1]
        p_open  = open_[i - 1]
        p_high  = high[i - 1]
        p_low   = low[i - 1]
        c_close = close[i]
        trend   = 1 if c_close > ema_v[i] else -1

        # ── Breakout detektálás ──────────────────────────────────────────────
        if not waiting:
            if p_close > swing_h + MIN_BREAKOUT_PTS and trend == 1:
                bo_dir, bo_level, bo_atr = 1, swing_h, cur_atr
                rt_count, waiting = 0, True
            elif p_close < swing_l - MIN_BREAKOUT_PTS and trend == -1:
                bo_dir, bo_level, bo_atr = -1, swing_l, cur_atr
                rt_count, waiting = 0, True

        # ── Retest várakozás ─────────────────────────────────────────────────
        else:
            rt_count += 1
            if rt_count > MAX_RETEST_CANDLES:
                waiting, bo_dir = False, 0
                continue

            zone_half  = bo_atr * RETEST_ZONE_PCT
            z_up       = bo_level + zone_half
            z_dn       = bo_level - zone_half
            entry_px   = c_close        # piacra lép a szignál gyertya zárásán

            if bo_dir == 1:
                touched = (p_low <= z_up) and (p_high >= z_dn)
                bullish = (p_close > p_open and
                           (p_close - p_open) > 0.3 * (p_high - p_low + 1e-9))
                if touched and bullish:
                    # Min SL: max(entry-low + buffer, ATR×0.5, 0.15% of price)
                    raw_sl = entry_px - p_low + bo_atr * 0.25
                    sl_d   = max(raw_sl, bo_atr * 0.5, entry_px * 0.0015)
                    tp_d   = sl_d * RISK_REWARD

                    # SL max 1.5% of price ellenőrzés (túl nagy SL → skip)
                    if sl_d / entry_px > 0.015:
                        waiting, bo_dir = False, 0
                        continue

                    long_e[i]   = True
                    sl_ratio[i] = sl_d / entry_px
                    tp_ratio[i] = tp_d / entry_px
                    # oz = risk_money / sl_per_oz  (max 30 oz cap)
                    size_usd[i] = min((INIT_CASH * MAX_RISK_PCT / 100.0) / sl_d, 30.0)

                    last_day   = curr_day
                    in_trade   = True
                    trade_end  = i + MAX_RETEST_CANDLES * 4
                    waiting, bo_dir = False, 0

            elif bo_dir == -1:
                touched = (p_high >= z_dn) and (p_low <= z_up)
                bearish = (p_close < p_open and
                           (p_open - p_close) > 0.3 * (p_high - p_low + 1e-9))
                if touched and bearish:
                    raw_sl = p_high - entry_px + bo_atr * 0.25
                    sl_d   = max(raw_sl, bo_atr * 0.5, entry_px * 0.0015)
                    tp_d   = sl_d * RISK_REWARD

                    if sl_d / entry_px > 0.015:
                        waiting, bo_dir = False, 0
                        continue

                    short_e[i]  = True
                    sl_ratio[i] = sl_d / entry_px
                    tp_ratio[i] = tp_d / entry_px
                    # oz = risk_money / sl_per_oz  (max 30 oz cap)
                    size_usd[i] = min((INIT_CASH * MAX_RISK_PCT / 100.0) / sl_d, 30.0)

                    last_day   = curr_day
                    in_trade   = True
                    trade_end  = i + MAX_RETEST_CANDLES * 4
                    waiting, bo_dir = False, 0

    return {
        "long_entries":  pd.Series(long_e,   index=df.index),
        "short_entries": pd.Series(short_e,  index=df.index),
        "sl_ratio":      pd.Series(sl_ratio, index=df.index),
        "tp_ratio":      pd.Series(tp_ratio, index=df.index),
        "size_usd":      pd.Series(size_usd, index=df.index),
    }


# ===========================================================================
# 4. VECTORBT PORTFÓLIÓ FUTTATÁS
# ===========================================================================

def build_portfolio(df: pd.DataFrame, signals: dict, direction: str) -> vbt.Portfolio:
    """
    direction: "longonly" | "shortonly"
    """
    close    = df["Close"]
    entries  = signals["long_entries"] if direction == "longonly" else signals["short_entries"]
    sl_ratio = signals["sl_ratio"]
    tp_ratio = signals["tp_ratio"]
    size_usd = signals["size_usd"]

    # Pozícióméret: size_usd / entry_price → darab (troy oz)
    # VectorBT size_type="value" → USD értékben adjuk meg → elegánsabb
    size_val = size_usd.copy()
    size_val[~entries] = np.nan   # csak belépési bárokon aktív

    # sl/tp_stop: VectorBT decimális arányban várja (az entry ártól)
    sl_s = sl_ratio.copy()
    tp_s = tp_ratio.copy()
    sl_s[~entries] = np.nan
    tp_s[~entries] = np.nan

    pf = vbt.Portfolio.from_signals(
        close       = close,
        entries     = entries,
        exits       = pd.Series(False, index=df.index),
        sl_stop     = sl_s,
        tp_stop     = tp_s,
        size        = size_val.fillna(0),
        size_type   = "amount",         # troy oz (egység)
        init_cash   = INIT_CASH,
        fees        = COMMISSION / close,  # fix USD jutalék → arány formában
        freq        = "15min",
        direction   = direction,
    )
    return pf


# ===========================================================================
# 5. RIPORTOK
# ===========================================================================

def section(title: str):
    print(f"\n{'═'*60}")
    print(f"  {title}")
    print(f"{'═'*60}")


def print_portfolio_stats(pf: vbt.Portfolio, name: str):
    section(name)
    stats = pf.stats()
    trades = pf.trades.records_readable

    init_v  = pf.init_cash
    final_v = pf.final_value()
    ret_pct = (final_v / init_v - 1) * 100

    print(f"  Induló tőke        : ${init_v:>10,.2f}")
    print(f"  Végső tőke         : ${final_v:>10,.2f}")
    print(f"  Összes hozam       : {ret_pct:>+9.2f}%")
    print(f"  Max Drawdown       : {stats.get('Max Drawdown [%]', 'N/A')}")
    print(f"  Sharpe Ratio       : {stats.get('Sharpe Ratio', 'N/A')}")
    print(f"  Sortino Ratio      : {stats.get('Sortino Ratio', 'N/A')}")
    print(f"  Win Rate           : {stats.get('Win Rate [%]', 'N/A')}")
    print(f"  Kötések (összes)   : {stats.get('Total Trades', len(trades))}")
    print(f"  Lezárt kötések     : {stats.get('Total Closed Trades', 'N/A')}")
    print(f"  Legjobb trade      : {stats.get('Best Trade [%]', 'N/A')}")
    print(f"  Legrosszabb trade  : {stats.get('Worst Trade [%]', 'N/A')}")
    print(f"  Profit Factor      : {stats.get('Profit Factor', 'N/A')}")
    print(f"  Expectancy         : {stats.get('Expectancy', 'N/A')}")
    return trades


def print_monthly_breakdown(trades_df: pd.DataFrame, label: str):
    if trades_df is None or len(trades_df) == 0:
        print(f"\n[{label}] Nincs lezárt trade.")
        return pd.DataFrame()

    # Oszlopok keresése
    entry_col = next((c for c in trades_df.columns if "Entry" in c and "Time" in c), None)
    pnl_col   = next((c for c in trades_df.columns if "PnL" in c), None)
    ret_col   = next((c for c in trades_df.columns if "Return" in c), None)

    if not entry_col or not pnl_col:
        print(f"[{label}] Hiányzó oszlopok: {list(trades_df.columns)}")
        return pd.DataFrame()

    df = trades_df.copy()
    df["Month"] = pd.to_datetime(df[entry_col]).dt.to_period("M")

    monthly = df.groupby("Month").agg(
        Kötés    = (pnl_col, "count"),
        Win_Rate = (pnl_col, lambda x: f"{(x > 0).mean() * 100:.0f}%"),
        PnL_USD  = (pnl_col, lambda x: f"${x.sum():+.2f}"),
        PnL_Pct  = (pnl_col, lambda x: f"{x.sum() / INIT_CASH * 100:+.2f}%"),
        Max_Loss = (pnl_col, lambda x: f"${x.min():.2f}"),
        Best     = (pnl_col, lambda x: f"${x.max():.2f}"),
    )
    print(f"\n  Havi bontás – {label}")
    print(f"  {'─'*56}")
    print(monthly.to_string())
    return monthly


def combined_monthly_summary(trades_long, trades_short):
    section("KOMBINÁLT HAVI TELJESÍTMÉNY (LONG + SHORT)")

    frames = [t for t in [trades_long, trades_short]
              if t is not None and len(t) > 0]
    if not frames:
        print("  Nincs lezárt trade az elemzéshez.")
        return

    all_t = pd.concat(frames, ignore_index=True)
    entry_col = next((c for c in all_t.columns if "Entry" in c and "Time" in c), None)
    pnl_col   = next((c for c in all_t.columns if "PnL" in c), None)
    if not entry_col or not pnl_col:
        return

    all_t["Month"] = pd.to_datetime(all_t[entry_col]).dt.to_period("M")

    monthly = all_t.groupby("Month").agg(
        Kötés    = (pnl_col, "count"),
        Win_Rate = (pnl_col, lambda x: f"{(x > 0).mean()*100:.0f}%"),
        PnL_USD  = (pnl_col, "sum"),
        PnL_Pct  = (pnl_col, lambda x: f"{x.sum() / INIT_CASH * 100:+.2f}%"),
    )
    print(monthly.assign(PnL_USD=monthly["PnL_USD"].map(lambda x: f"${x:+.2f}")).to_string())

    total_pnl  = all_t[pnl_col].sum()
    total_pct  = total_pnl / INIT_CASH * 100
    win_rate   = (all_t[pnl_col] > 0).mean() * 100
    n_months   = all_t["Month"].nunique()
    avg_month  = total_pct / n_months if n_months else 0

    print(f"\n  {'─'*56}")
    print(f"  Összes PnL         : ${total_pnl:+,.2f}  ({total_pct:+.2f}%)")
    print(f"  Átlag havi hozam   : {avg_month:+.2f}%")
    print(f"  Összes Win Rate    : {win_rate:.1f}%")
    print(f"  Hónapok száma      : {n_months}")
    print(f"  Összes kötés       : {len(all_t)}")
    print(f"  Kötés/hónap átlag  : {len(all_t)/n_months:.1f}")

    target_met = avg_month >= 5.0
    marker = "✓" if target_met else "✗"
    gap    = "" if target_met else f"  ({5.0 - avg_month:.1f}% hiány)"
    print(f"\n  Havi 5% cél  [{marker}] : {avg_month:+.2f}%{gap}")
    print(f"  Break-even WR      : {100/(1+RISK_REWARD):.1f}%  (1:{RISK_REWARD} R:R)")
    print(f"  {'─'*56}")


# ===========================================================================
# 6. MAIN
# ===========================================================================

if __name__ == "__main__":
    section("XAUUSD M15 Breakout & Retest – VectorBT Backtest v2")
    print(f"""
  Swing lookback    : {SWING_LOOKBACK} gyertya
  Max retest candle : {MAX_RETEST_CANDLES}
  Retest zóna       : ATR × {RETEST_ZONE_PCT}
  Min breakout      : ${MIN_BREAKOUT_PTS}
  Risk:Reward       : 1:{RISK_REWARD}
  Max kockázat      : {MAX_RISK_PCT}% / trade
  Session           : {SESSION_START}:00–{SESSION_END}:00 UTC
  Induló tőke       : ${INIT_CASH:,}
""")

    # ── Adatbetöltés ─────────────────────────────────────────────────────────
    import os
    MT5_CSV = "XAUUSD_M15.csv"
    if os.path.exists(MT5_CSV):
        print(f"MT5 CSV betöltése: {MT5_CSV}")
        df = load_mt5_csv(MT5_CSV)
    else:
        print("MT5 CSV nem található → szintetikus XAUUSD M15 adat generálása (2 év)...")
        df = generate_synthetic_xauusd(years=2)

    avg_atr = atr_series(df["High"], df["Low"], df["Close"], ATR_PERIOD).mean()
    print(f"Betöltve: {len(df):,} gyertya | {df.index[0]} → {df.index[-1]}")
    print(f"Ár tartomány: ${df['Close'].min():.2f} – ${df['Close'].max():.2f}")
    print(f"Átlag ATR   : ${avg_atr:.2f}")

    # ── Szignálok generálása ─────────────────────────────────────────────────
    print("\nSzignálok generálása...")
    signals = generate_signals(df)
    n_long  = signals["long_entries"].sum()
    n_short = signals["short_entries"].sum()
    print(f"  Long belépők  : {n_long}")
    print(f"  Short belépők : {n_short}")
    print(f"  Összes        : {n_long + n_short}")

    if (n_long + n_short) == 0:
        print("\nHIBA: Nincs egyetlen szignál sem!")
        print("Próbálj kisebb MIN_BREAKOUT_PTS vagy nagyobb RETEST_ZONE_PCT értékkel.")
        exit(1)

    # ── Portfóliók építése ───────────────────────────────────────────────────
    pf_long  = build_portfolio(df, signals, "longonly")  if n_long  > 0 else None
    pf_short = build_portfolio(df, signals, "shortonly") if n_short > 0 else None

    # ── Statisztikák ─────────────────────────────────────────────────────────
    t_long  = print_portfolio_stats(pf_long,  "LONG PORTFÓLIÓ")  if pf_long  else None
    t_short = print_portfolio_stats(pf_short, "SHORT PORTFÓLIÓ") if pf_short else None

    # ── Havi bontás ──────────────────────────────────────────────────────────
    print_monthly_breakdown(t_long,  "Long")
    print_monthly_breakdown(t_short, "Short")
    combined_monthly_summary(t_long, t_short)

    # ── VectorBT full stats ──────────────────────────────────────────────────
    section("VECTORBT RÉSZLETES STATS – LONG")
    if pf_long:
        print(pf_long.stats().to_string())

    section("VECTORBT RÉSZLETES STATS – SHORT")
    if pf_short:
        print(pf_short.stats().to_string())

    print("\n[KÉSZ] Backtest befejezve.")
    print("       Valós MT5 adathoz: másold a XAUUSD_M15.csv-t ebbe a mappába.")
