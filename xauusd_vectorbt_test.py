"""
XAUUSD M15 Multi-Timeframe Breakout & Retest Strategy – VectorBT Backtest (v5)
===============================================================================
Stratégia logika – Hármas piac-fázis felismerés:
  1. D1 EMA50 (emelkedő)  → havi trend proxy (ár EMA felett ÉS EMA emelkedik)
  2. W1 EMA10             → heti trend (ár EMA felett)
  3. W1 momentum          → heti lendület (ez a hét > előző hét)
  4. H4 EMA20             → közepes-távú belépési irány megerősítés
  5. H4 ADX(14) ≥ 20      → trend erősség szűrő (choppy piacban nincs kereskedés)
  6. M15 Breakout + Retest → pontos belépési szignál

  Fázisok:
    BULL (bias=+1): D1↑ + W1↑ + W1mom↑ + H4EMA↑ + ADX≥20  → Long breakout
    CHOP (bias=0):  bármely feltétel hiányzik                → nincs kereskedés
    (Short kereskedés kikapcsolva – XAUUSD long-only)

Célok:
  - Havi ~5% profit (2025-ös trending piacon: ~4.9%/hó)
  - Max 2 kötés/nap
  - Max SL 1% / trade (FTMO szabály)
  - Max DD < 10% (FTMO limit: 5.76% realizált)

Futtatás:
    python xauusd_vectorbt_test.py

Függőségek (kötelező):
    pip install vectorbt pandas numpy

Függőségek (opcionális, MT5 API-hoz):
    pip install MetaTrader5   # Windows/Wine szükséges
    pip install tvdatafeed    # TradingView scraper (cross-platform)
"""

import os
import glob
import numpy as np
import pandas as pd
import vectorbt as vbt
import warnings

warnings.filterwarnings("ignore")
np.random.seed(42)

# ===========================================================================
# PARAMÉTEREK
# ===========================================================================
TIMEFRAME           = "M15"   # "M5" vagy "M15"
SYMBOL              = "XAUUSD"
YEARS_HISTORY       = 2       # MT5/tvdatafeed lekérési időszak

# ── M15 Breakout paraméterek ────────────────────────────────────────────────
SWING_LOOKBACK      = 20      # Swing high/low ablak (M15 gyertyák)
MAX_RETEST_CANDLES  = 10      # Max várakozás retestre (gyertyák)
RETEST_ZONE_PCT     = 0.30    # Retest zóna tűrése
MIN_BREAKOUT_PTS    = 0.60    # Min breakout méret ($)
ATR_PERIOD          = 14
SWING_LEVELS        = [8, 15, 25]  # Párhuzamos swing szintek (több szignál)

# ── HTF Trend paraméterek – Hármas piac-fázis felismerés ───────────────────
# HTF_MODE lehetőségek:
#   "three_phase" – D1 EMA50 (havi) + W1 EMA10 (heti) + H4 ADX(14) erősség
#                   Bull (D1↑+W1↑+ADX>20) → Long    |
#                   Bear (D1↓+W1↓+ADX>20) → Short   |
#                   Chop (ADX<20 / konfliktus) → nincs kereskedés
#   "strict"      – W1+H1 mindkettő egyezés kell
#   "w1_only"     – csak W1 EMA10 iránytól függ
HTF_MODE            = "three_phase"
D1_EMA_PERIOD       = 50      # D1 EMA50 → ~2.5 havi trend (havi proxy)
W1_EMA_PERIOD       = 10      # W1 EMA10 → heti trend
H1_EMA_PERIOD       = 50      # H1 EMA50 → óra trend (strict/w1_only módban)
ADX_PERIOD          = 14      # H4 ADX periódus
ADX_CHOP_THRESHOLD  = 20      # ADX alatt = choppy piac → nincs kereskedés

# ── Kockázatkezelés ─────────────────────────────────────────────────────────
# STRATEGY_MODE:
#   "fixed_tp"  – fix TP = SL × RISK_REWARD
#   "trailing"  – trailing stop, nincs fix TP (a nyertesek futnak)
STRATEGY_MODE       = "fixed_tp"  # "fixed_tp" | "trailing"
RISK_REWARD         = 4.0     # TP = SL × 4.0 (= 3.2% TP, ha SL=0.8%) – legjobb EV
ATR_SL_MULT         = 2.0     # (referencia, nem használt)
TRAIL_STOP_PCT      = 0.008   # SL távolság %-ban – MINDKÉT módban pozícióméret alap
LONG_RISK_PCT       = 1.0     # Long kockázat max 1% of equity (FTMO szabály)
SHORT_RISK_PCT      = 0.0     # Short kockázat – 0 = kikapcsolva (gold long-only)
MAX_RISK_PCT        = max(LONG_RISK_PCT, SHORT_RISK_PCT)
MAX_SL_PCT          = 0.020   # Max SL távolság (2% of price, skip ha szélesebb)
MAX_TRADES_PER_DAY  = 2       # Max kötés naponta

# ── FTMO Prop Trading Szabályok ─────────────────────────────────────────────
# FTMO Challenge/Funded: max napi veszteség 5%, max összes DD 10%
# Stratégia biztonsági margóval:
FTMO_DAILY_LOSS_LIMIT  = 0.045   # 4.5% – ha elér, ne nyisson új trade-t aznap
FTMO_MAX_DD_LIMIT      = 0.090   # 9% – ha összes DD eléri, leállítás (10% FTMO limit)
FTMO_ACCOUNT_SIZE      = 10_000  # FTMO Challenge standard méret

# ── Session és tőke ─────────────────────────────────────────────────────────
SESSION_START       = 7       # UTC óra
SESSION_END         = 18
INIT_CASH           = FTMO_ACCOUNT_SIZE
COMMISSION          = 0.60    # USD / oz round-trip (FTMO XAUUSD: spread ~$0.30 + comm ~$0.30)
#                               Megjegyzés: FTMO XAUUSD min spread ≈ 0.2 USD, tipikus ≈ 0.35 USD

# ── Backtest szűrési beállítások ─────────────────────────────────────────────
# BACKTEST_YEAR: ha meg van adva, csak az adott év kereskedéseit elemzi.
#   – HTF indikátorok (EMA, ADX) a TELJES adatsoron számítva (2022-tól)
#     → megfelelő bemelegedési periódus, nincs jövőbeli szivárgás
#   – Portfólió friss $10,000-ral indul a kiválasztott év jan. 1-jén
#   None = teljes adatsor (2022–2025)
BACKTEST_YEAR       = 2025    # None | 2022 | 2023 | 2024 | 2025


# ===========================================================================
# 1. ADATBETÖLTÉS / GENERÁLÁS
# ===========================================================================

# MT5 timeframe map
_MT5_TF = {"M1": 1, "M5": 5, "M15": 15, "M30": 30, "H1": 16385, "H4": 16388, "D1": 16408}

# VectorBT freq string map
_VBT_FREQ = {"M5": "5min", "M15": "15min"}


def load_from_mt5(symbol: str = SYMBOL, tf: str = TIMEFRAME, years: int = YEARS_HISTORY) -> pd.DataFrame:
    """
    Historikus OHLCV adat letöltése a futó MetaTrader 5 terminálból.
    Szükséges: telepített MT5, futó terminál, bejelentkezve (demo is OK).

    pip install MetaTrader5
    """
    try:
        import MetaTrader5 as mt5
        from datetime import datetime, timedelta

        if not mt5.initialize():
            raise RuntimeError(f"MT5 initialize() sikertelen: {mt5.last_error()}")

        tf_code = getattr(mt5, f"TIMEFRAME_{tf}", None)
        if tf_code is None:
            raise ValueError(f"Ismeretlen timeframe: {tf}")

        date_to   = datetime.utcnow()
        date_from = date_to - timedelta(days=365 * years)

        rates = mt5.copy_rates_range(symbol, tf_code, date_from, date_to)
        mt5.shutdown()

        if rates is None or len(rates) == 0:
            raise RuntimeError("Nincs adat – ellenőrizd a symbol nevet és az MT5 kapcsolatot.")

        df = pd.DataFrame(rates)
        df["time"] = pd.to_datetime(df["time"], unit="s")
        df.set_index("time", inplace=True)
        df.index.name = None
        df = df.rename(columns={
            "open": "Open", "high": "High", "low": "Low",
            "close": "Close", "tick_volume": "Volume"
        })
        return df[["Open", "High", "Low", "Close", "Volume"]].astype(float)

    except ImportError:
        raise ImportError("MetaTrader5 csomag nem található. Telepítsd: pip install MetaTrader5")


def load_from_tvdatafeed(symbol: str = SYMBOL, tf: str = TIMEFRAME,
                         years: int = YEARS_HISTORY) -> pd.DataFrame:
    """
    Historikus OHLCV adat letöltése TradingView-ból (tvdatafeed scraper).
    Cross-platform, MT5 terminál NEM szükséges.

    pip install tvdatafeed
    """
    try:
        from tvdatafeed import TvDatafeed, Interval

        _tv_interval = {
            "M1":  Interval.in_1_minute,
            "M5":  Interval.in_5_minute,
            "M15": Interval.in_15_minute,
            "M30": Interval.in_30_minute,
            "H1":  Interval.in_1_hour,
            "H4":  Interval.in_4_hour,
            "D1":  Interval.in_daily,
        }
        if tf not in _tv_interval:
            raise ValueError(f"Ismeretlen timeframe: {tf}")

        bars_per_year = {"M5": 365 * 24 * 12, "M15": 365 * 24 * 4}.get(tf, 100_000)
        n_bars = bars_per_year * years

        print(f"  tvdatafeed: {symbol} {tf}, kért gyertyák: {n_bars:,}...")
        tv = TvDatafeed()

        # OANDA XAUUSD spot (legjobb minőség)
        for exchange in ["OANDA", "FXCM", "TVC"]:
            try:
                df = tv.get_hist(symbol, exchange,
                                 interval=_tv_interval[tf],
                                 n_bars=min(n_bars, 20_000))  # API limit
                if df is not None and len(df) > 100:
                    df = df.rename(columns={
                        "open": "Open", "high": "High", "low": "Low",
                        "close": "Close", "volume": "Volume"
                    })
                    df.index = pd.to_datetime(df.index)
                    df.index.name = None
                    print(f"  Forrás: {exchange}, gyertyák: {len(df):,}")
                    return df[["Open", "High", "Low", "Close", "Volume"]].astype(float).dropna()
            except Exception:
                continue
        raise RuntimeError("tvdatafeed: minden exchange megpróbálva, nincs adat.")

    except ImportError:
        raise ImportError("tvdatafeed csomag nem található. Telepítsd: pip install tvdatafeed")


def load_mt5_csv(filepath: str) -> pd.DataFrame:
    """
    Általános OHLCV CSV betöltő – támogatott formátumok:
      - MT5 tab-elválasztós export: <DATE>\t<TIME>\t<OPEN>...
      - Standard comma CSV:         datetime,Open,High,Low,Close,Volume
    """
    # Próbáljuk kitalálni az elválasztót
    with open(filepath, "r") as f:
        first_line = f.readline()
    sep = "\t" if "\t" in first_line else ","

    df = pd.read_csv(filepath, sep=sep, header=0)
    df.columns = [c.strip().lower().lstrip("<").rstrip(">") for c in df.columns]

    # Datetime index felépítése
    if "datetime" in df.columns:
        df.index = pd.to_datetime(df["datetime"])
        df = df.drop(columns=["datetime"])
    elif "date" in df.columns and "time" in df.columns:
        df.index = pd.to_datetime(df["date"].astype(str) + " " + df["time"].astype(str))
        df = df.drop(columns=["date", "time"])
    elif "date" in df.columns:
        df.index = pd.to_datetime(df["date"])
        df = df.drop(columns=["date"])
    else:
        df.index = pd.to_datetime(df.index)

    df.index.name = None

    # Egységes oszlopnevek
    rename = {"tickvol": "volume", "vol": "volume", "tick_volume": "volume"}
    df = df.rename(columns=rename)

    keep = [c for c in ["open", "high", "low", "close", "volume"] if c in df.columns]
    df = df[keep]
    df.columns = [c.capitalize() for c in df.columns]
    return df.astype(float).dropna().sort_index()


def generate_synthetic_xauusd(years: int = 2, freq: str = "15min") -> pd.DataFrame:
    """
    Szintetikus XAUUSD OHLCV adatsor GBM alapon.
    Csak kereskedési napokat tartalmaz (hétfő–péntek).
    freq: '5min' | '15min'
    """
    bars_per_day = 24 * 60 // int(freq.replace("min", ""))
    all_times = pd.date_range(
        start="2023-01-02 00:00", periods=years * 252 * bars_per_day, freq=freq
    )
    mask = all_times.weekday < 5
    idx  = all_times[mask]
    n    = len(idx)

    # GBM paraméterek (XAUUSD reális)
    start_price  = 1_855.0
    daily_vol    = 0.011       # ~1.1% napi volatilitás
    bar_vol      = daily_vol / np.sqrt(bars_per_day)
    bar_drift    = 0.00003 / bars_per_day

    # Alap log-hozamok
    lr = np.random.normal(bar_drift, bar_vol, n)

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
    bar_rng = np.abs(np.random.normal(0, bar_vol * 1.8, n)) * close
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


def adx_series(high, low, close, period=14) -> pd.Series:
    """
    Average Directional Index (ADX) – trend erősség mérő.
    ADX > 20: trending piac (kereskedünk)
    ADX < 20: choppy/oldalazó piac (nem kereskedünk)
    """
    up   = high.diff().clip(lower=0)
    down = (-low.diff()).clip(lower=0)
    dm_plus  = up.where(up > down, 0.0)
    dm_minus = down.where(down > up, 0.0)

    tr = pd.concat([
        high - low,
        (high - close.shift(1)).abs(),
        (low  - close.shift(1)).abs(),
    ], axis=1).max(axis=1)

    alpha = 1.0 / period
    atr_s  = tr.ewm(alpha=alpha, adjust=False).mean()
    dmp_s  = dm_plus.ewm(alpha=alpha, adjust=False).mean()
    dmm_s  = dm_minus.ewm(alpha=alpha, adjust=False).mean()

    di_plus  = 100 * dmp_s / atr_s.replace(0, np.nan)
    di_minus = 100 * dmm_s / atr_s.replace(0, np.nan)
    dx = 100 * (di_plus - di_minus).abs() / (di_plus + di_minus).replace(0, np.nan)
    return dx.ewm(alpha=alpha, adjust=False).mean().fillna(0)


def compute_htf_bias(df: pd.DataFrame) -> pd.Series:
    """
    Kiszámolja a magasabb időkeretű (HTF) irányt az M15 adatból.

    W1 (heti) EMA10 + H1 EMA50 alapján:
      +1 = mindkét HTF bullish  → csak LONG belépések engedélyezve
      -1 = mindkét HTF bearish  → csak SHORT belépések engedélyezve
       0 = konfliktus            → nincs kereskedés (ha HTF_MODE="strict")
              vagy = W1 irány   (ha HTF_MODE="w1_only")

    A bias értéke az M15 index-re van visszavetítve (ffill).

    Fázisok (three_phase módban):
      +1 = BULL: D1 EMA50↑ + W1 EMA10↑ + H4 ADX≥20  → Long breakout
      -1 = BEAR: D1 EMA50↓ + W1 EMA10↓ + H4 ADX≥20  → Short breakout
       0 = CHOP: ADX<20 VAGY D1/W1 konfliktus         → nincs kereskedés
    """
    # ── D1 trend (havi proxy: ~50 nap ≈ 2.5 hónap) ──────────────────────────
    d1 = df["Close"].resample("D").last().dropna()
    d1_ema = d1.ewm(span=D1_EMA_PERIOD, adjust=False).mean()
    # Dupla feltétel: ár EMA felett ÉS EMA emelkedő (slope > 0 az elmúlt 5 napban)
    d1_ema_rising = d1_ema > d1_ema.shift(5)
    d1_bull = ((d1 > d1_ema.shift(1)) & d1_ema_rising).astype(int).replace(0, -1)
    d1_m15  = d1_bull.reindex(df.index, method="ffill").fillna(0)

    # ── W1 trend (heti EMA10) ─────────────────────────────────────────────────
    w1 = df["Close"].resample("W").last().dropna()
    w1_ema = w1.ewm(span=W1_EMA_PERIOD, adjust=False).mean()
    w1_bull = (w1 > w1_ema.shift(1)).astype(int).replace(0, -1)
    w1_m15  = w1_bull.reindex(df.index, method="ffill").fillna(0)

    # W1 momentum: aktuális hét > előző hét zárója (lendület megerősítés)
    w1_momentum = (w1 > w1.shift(1)).astype(int)
    w1_mom_m15  = w1_momentum.reindex(df.index, method="ffill").fillna(0)

    # ── H1 trend (H1 EMA50, strict/w1_only módhoz) ───────────────────────────
    h1 = df["Close"].resample("1h").last().dropna()
    h1_ema = h1.ewm(span=H1_EMA_PERIOD, adjust=False).mean()
    h1_bull = (h1 > h1_ema.shift(1)).astype(int).replace(0, -1)
    h1_m15  = h1_bull.reindex(df.index, method="ffill").fillna(0)

    # ── H4 adatok (ADX + EMA20) ───────────────────────────────────────────────
    h4 = df.resample("4h").agg({
        "High": "max", "Low": "min", "Close": "last"
    }).dropna()

    # H4 ADX (trend erősség – choppy szűrő)
    h4_adx   = adx_series(h4["High"], h4["Low"], h4["Close"], ADX_PERIOD)
    adx_m15  = h4_adx.reindex(df.index, method="ffill").fillna(0)
    trending = (adx_m15 >= ADX_CHOP_THRESHOLD)

    # H4 EMA20 (közepes-távú belépési irány konfirmáció)
    h4_ema20  = h4["Close"].ewm(span=20, adjust=False).mean()
    h4_bull   = (h4["Close"] > h4_ema20).astype(int).replace(0, -1)
    h4_ema_m15 = h4_bull.reindex(df.index, method="ffill").fillna(0)

    # ── Kombináció ────────────────────────────────────────────────────────────
    bias = pd.Series(0, index=df.index, dtype=int)

    if HTF_MODE == "three_phase":
        # BULL: D1(emelkedő EMA) + W1 + H4 EMA20 mind bullish + ADX trending
        #       + W1 momentum: ez a hét zárja magasabban mint az előző (lendület)
        bull_cond = (trending & (d1_m15 == 1) & (w1_m15 == 1)
                     & (h4_ema_m15 == 1) & (w1_mom_m15 == 1))
        # BEAR: D1 + W1 + H4 EMA mind bearish + ADX trending
        bear_cond = trending & (d1_m15 == -1) & (w1_m15 == -1) & (h4_ema_m15 == -1)
        bias[bull_cond] =  1
        bias[bear_cond] = -1
        # CHOP: ADX<20 VAGY bármely HTF konfliktus → bias=0 → nincs kereskedés

    elif HTF_MODE == "strict":
        bias[(w1_m15 == 1) & (h1_m15 == 1)]  =  1
        bias[(w1_m15 == -1) & (h1_m15 == -1)] = -1

    else:  # w1_only
        bias = w1_m15.astype(int)

    return bias


# ===========================================================================
# 3. SZIGNÁL GENERÁLÁS
# ===========================================================================

def generate_signals(df: pd.DataFrame,
                     htf_bias: pd.Series = None) -> dict:
    """
    Multi-Timeframe Breakout & Retest szignálgenerátor.

    Paraméterek:
      df        – M15 OHLCV DataFrame
      htf_bias  – Előre kiszámított HTF bias Series (opcionális).
                  Ha None, belsőleg kiszámítja compute_htf_bias(df)-vel.
                  Ha megadott (pl. teljes adatsoron számítva), nincs jövőbeli szivárgás.

    Belépési logika:
      1. HTF bias (D1 EMA50 + W1 EMA10 + W1 mom + H4 EMA20 + H4 ADX ≥ 20)
      2. M15 breakout az HTF irányában
      3. Retest konfirmáció bullish/bearish gyertyával
      4. SL/TP számítás TRAIL_STOP_PCT × RISK_REWARD alapon

    Visszatér:
      long_entries, short_entries  (bool Series)
      sl_ratio, tp_ratio           (float Series – arány entry price-hoz)
      size_usd                     (float Series – oz darabszám)
    """
    if htf_bias is None:
        print("  HTF bias számítása (helyi adatsoron)...")
        htf_bias = compute_htf_bias(df)
    else:
        print("  HTF bias: előre számított (teljes adatsor → nincs jövőszivárgás)")

    # Igazítsuk a bias-t a df indexéhez (BACKTEST_YEAR esetén lehet eltérés)
    htf_bias = htf_bias.reindex(df.index, method="ffill").fillna(0).astype(int)

    # Statisztika
    long_bias_pct  = (htf_bias == 1).mean()  * 100
    short_bias_pct = (htf_bias == -1).mean() * 100
    neut_bias_pct  = (htf_bias == 0).mean()  * 100
    print(f"  HTF irány: Long {long_bias_pct:.0f}% | Short {short_bias_pct:.0f}% | Semleges {neut_bias_pct:.0f}%")

    n      = len(df)
    close  = df["Close"].values
    high   = df["High"].values
    low    = df["Low"].values
    open_  = df["Open"].values
    idx    = df.index
    bias_v = htf_bias.values

    atr_v  = atr_series(df["High"], df["Low"], df["Close"], ATR_PERIOD).values
    hours  = idx.hour

    # Párhuzamos swing szintek (SWING_LEVELS = [8, 15, 25])
    swing_highs = [df["High"].shift(2).rolling(lb).max().values for lb in SWING_LEVELS]
    swing_lows  = [df["Low"].shift(2).rolling(lb).min().values  for lb in SWING_LEVELS]
    n_lvl = len(SWING_LEVELS)

    long_e   = np.zeros(n, dtype=bool)
    short_e  = np.zeros(n, dtype=bool)
    sl_ratio = np.full(n, np.nan)
    tp_ratio = np.full(n, np.nan)
    size_usd = np.zeros(n)

    # Állapotgép – egy per swing szint
    bo_dir   = [0]   * n_lvl
    bo_level = [0.0] * n_lvl
    bo_atr_l = [0.0] * n_lvl
    rt_count = [0]   * n_lvl
    waiting  = [False] * n_lvl

    last_day        = None
    trades_today    = 0
    daily_pnl_frac  = 0.0   # napi PnL tört (FTMO napi limit követése)
    equity          = float(INIT_CASH)
    ftmo_stopped    = False  # ha max DD eléri, leállítás

    for i in range(max(SWING_LEVELS) + 5, n - 1):

        # FTMO equity stop – ha 9%-ot esett az equity, ne nyisson új trade-t
        if ftmo_stopped:
            continue

        curr_day = idx[i].date()
        if curr_day != last_day:
            trades_today   = 0
            daily_pnl_frac = 0.0
            last_day       = curr_day

        # FTMO napi veszteség limit: ha nap közben -4.5% → állj le aznap
        if daily_pnl_frac <= -FTMO_DAILY_LOSS_LIMIT:
            continue

        if trades_today >= MAX_TRADES_PER_DAY:
            continue
        if not (SESSION_START <= int(hours[i]) < SESSION_END):
            continue

        htf = bias_v[i]
        if htf == 0:
            continue

        cur_atr = atr_v[i]
        if np.isnan(cur_atr) or cur_atr <= 0:
            continue

        p_c = close[i - 1];  p_o = open_[i - 1]
        p_h = high[i - 1];   p_l = low[i - 1]
        c_c = close[i]

        entry_taken = False

        for lvl in range(n_lvl):
            if entry_taken:
                break

            sh = swing_highs[lvl][i]
            sl = swing_lows[lvl][i]
            if np.isnan(sh) or np.isnan(sl):
                continue

            # ── Breakout detektálás ──────────────────────────────────────────
            if not waiting[lvl]:
                if htf == 1 and p_c > sh + MIN_BREAKOUT_PTS:
                    bo_dir[lvl], bo_level[lvl], bo_atr_l[lvl] = 1, sh, cur_atr
                    rt_count[lvl] = 0;  waiting[lvl] = True
                elif htf == -1 and p_c < sl - MIN_BREAKOUT_PTS:
                    bo_dir[lvl], bo_level[lvl], bo_atr_l[lvl] = -1, sl, cur_atr
                    rt_count[lvl] = 0;  waiting[lvl] = True

            # ── Retest várakozás ─────────────────────────────────────────────
            else:
                if (bo_dir[lvl] == 1 and htf != 1) or (bo_dir[lvl] == -1 and htf != -1):
                    waiting[lvl], bo_dir[lvl] = False, 0
                    continue

                rt_count[lvl] += 1
                if rt_count[lvl] > MAX_RETEST_CANDLES:
                    waiting[lvl], bo_dir[lvl] = False, 0
                    continue

                zone = bo_atr_l[lvl] * RETEST_ZONE_PCT
                z_up = bo_level[lvl] + zone
                z_dn = bo_level[lvl] - zone

                if bo_dir[lvl] == 1:  # ── LONG ───────────────────────────────
                    touched = (p_l <= z_up) and (p_h >= z_dn)
                    bullish = (p_c > p_o and (p_c - p_o) > 0.25 * (p_h - p_l + 1e-9))
                    if touched and bullish:
                        # Mindkét módban TRAIL_STOP_PCT alapú SL (0.8% → 5 oz pozíció)
                        # fixed_tp: fix SL + fix TP=3× (nincs trailing)
                        # trailing:  trailing SL, nincs TP
                        sl_d = c_c * TRAIL_STOP_PCT
                        if STRATEGY_MODE == "fixed_tp" and sl_d / c_c > MAX_SL_PCT:
                            waiting[lvl], bo_dir[lvl] = False, 0
                            continue
                        risk_amount = equity * LONG_RISK_PCT / 100.0
                        long_e[i]   = True
                        sl_ratio[i] = sl_d / c_c
                        tp_ratio[i] = (sl_d * RISK_REWARD) / c_c if STRATEGY_MODE == "fixed_tp" else np.nan
                        size_usd[i] = min(risk_amount / sl_d, 35.0)
                        # FTMO: becsült veszteség hatása a napi PnL-re
                        est_loss = size_usd[i] * sl_d
                        daily_pnl_frac -= est_loss / INIT_CASH
                        if (equity - est_loss) / INIT_CASH < (1.0 - FTMO_MAX_DD_LIMIT):
                            ftmo_stopped = True
                        trades_today += 1
                        waiting[lvl], bo_dir[lvl] = False, 0
                        entry_taken = True
                        # Töröljük a többi szint pending breakoutját is
                        for k in range(n_lvl):
                            waiting[k], bo_dir[k] = False, 0

                elif bo_dir[lvl] == -1 and SHORT_RISK_PCT > 0:  # ── SHORT ───
                    touched = (p_h >= z_dn) and (p_l <= z_up)
                    bearish = (p_c < p_o and (p_o - p_c) > 0.25 * (p_h - p_l + 1e-9))
                    if touched and bearish:
                        sl_d = c_c * TRAIL_STOP_PCT  # short is: same % SL
                        if STRATEGY_MODE == "fixed_tp" and sl_d / c_c > MAX_SL_PCT:
                            waiting[lvl], bo_dir[lvl] = False, 0
                            continue
                        risk_amount_s = equity * SHORT_RISK_PCT / 100.0
                        short_e[i]  = True
                        sl_ratio[i] = sl_d / c_c
                        tp_ratio[i] = (sl_d * RISK_REWARD) / c_c if STRATEGY_MODE == "fixed_tp" else np.nan
                        size_usd[i] = min(risk_amount_s / sl_d, 35.0)
                        trades_today += 1
                        waiting[lvl], bo_dir[lvl] = False, 0
                        entry_taken = True
                        for k in range(n_lvl):
                            waiting[k], bo_dir[k] = False, 0
                else:
                    # Short kihagyva (bull piac)
                    if bo_dir[lvl] == -1:
                        waiting[lvl], bo_dir[lvl] = False, 0

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

    STRATEGY_MODE = "fixed_tp"  → fix SL + fix TP
    STRATEGY_MODE = "trailing"  → trailing stop, nincs fix TP (nyertesek futnak)
    """
    close   = df["Close"]
    entries = signals["long_entries"] if direction == "longonly" else signals["short_entries"]

    size_val = signals["size_usd"].copy()
    size_val[~entries] = np.nan

    sl_s = signals["sl_ratio"].copy()
    sl_s[~entries] = np.nan

    tp_s = signals["tp_ratio"].copy()
    tp_s[~entries] = np.nan

    common = dict(
        close      = close,
        entries    = entries,
        exits      = pd.Series(False, index=df.index),
        size       = size_val.fillna(0),
        size_type  = "amount",
        init_cash  = INIT_CASH,
        fees       = COMMISSION / close,
        freq       = "15min",
        direction  = direction,
    )

    if STRATEGY_MODE == "trailing":
        # 0.5% trailing stop → 8 oz pozíció $2500-on → nagyobb dolláros nyeremény
        # Az SL az árral együtt mozog felfelé (trailing), nincs fix TP.
        pf = vbt.Portfolio.from_signals(
            **common,
            sl_stop  = TRAIL_STOP_PCT,   # 0.5% trailing distance
            sl_trail = True,             # trailing logika aktiválva
            tp_stop  = None,             # nincs fix TP – nyertesek futnak
        )
    else:  # fixed_tp
        pf = vbt.Portfolio.from_signals(
            **common,
            sl_stop  = sl_s,
            tp_stop  = tp_s,
            sl_trail = False,
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

    init_v    = pf.init_cash
    final_v   = pf.final_value()
    ret_pct   = (final_v / init_v - 1) * 100
    total_fees = float(stats.get("Total Fees Paid", 0))
    n_closed   = int(stats.get("Total Closed Trades", len(trades)))
    fee_per_t  = total_fees / n_closed if n_closed > 0 else 0

    dd_pct    = float(stats.get("Max Drawdown [%]", 0))
    ftmo_ok   = dd_pct <= 10.0

    print(f"  Induló tőke        : ${init_v:>10,.2f}")
    print(f"  Végső tőke         : ${final_v:>10,.2f}")
    print(f"  Összes hozam       : {ret_pct:>+9.2f}%")
    print(f"  Max Drawdown       : {dd_pct:>8.2f}%  {'✓ FTMO OK' if ftmo_ok else '✗ FTMO LIMIT!'}")
    print(f"  Sharpe Ratio       : {stats.get('Sharpe Ratio', 'N/A')}")
    print(f"  Sortino Ratio      : {stats.get('Sortino Ratio', 'N/A')}")
    print(f"  Calmar Ratio       : {stats.get('Calmar Ratio', 'N/A')}")
    print(f"  Win Rate           : {stats.get('Win Rate [%]', 'N/A')}")
    print(f"  Kötések (összes)   : {stats.get('Total Trades', len(trades))}")
    print(f"  Lezárt kötések     : {n_closed}")
    print(f"  Legjobb trade      : {stats.get('Best Trade [%]', 'N/A')}")
    print(f"  Legrosszabb trade  : {stats.get('Worst Trade [%]', 'N/A')}")
    print(f"  Profit Factor      : {stats.get('Profit Factor', 'N/A')}")
    print(f"  Expectancy         : {stats.get('Expectancy', 'N/A')}")
    print(f"  ── FTMO Jutalék ─────────────────────────────────────")
    print(f"  Kif. jutalék össz. : ${total_fees:>10,.2f}  ({total_fees/init_v*100:.2f}% of account)")
    print(f"  Jutalék / trade    : ${fee_per_t:>10,.2f}")
    print(f"  Jutalék / oz       : ${COMMISSION:.2f}  (FTMO XAUUSD round-trip)")
    print(f"  Hozam (díj nélkül) : {(final_v + total_fees)/init_v*100 - 100:>+9.2f}%  (gross)")
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
    section("XAUUSD M15 Multi-TF Breakout & Retest – VectorBT Backtest v5")
    print(f"""
  ── HTF Trend szűrők ─────────────────────────────
  W1 EMA periódus   : {W1_EMA_PERIOD} hét
  H1 EMA periódus   : {H1_EMA_PERIOD} óra
  HTF mód           : {HTF_MODE}  (strict = W1+H1 egyezés kell)

  ── M15 Breakout paraméterek ─────────────────────
  Swing lookback    : {SWING_LOOKBACK} gyertya
  Max retest candle : {MAX_RETEST_CANDLES}
  Retest zóna       : ATR × {RETEST_ZONE_PCT}
  Min breakout      : ${MIN_BREAKOUT_PTS}

  ── Kockázatkezelés ──────────────────────────────
  Risk:Reward       : 1:{RISK_REWARD}
  Max kockázat      : {MAX_RISK_PCT}% / trade
  Max SL távolság   : {MAX_SL_PCT*100:.1f}% of price
  Session           : {SESSION_START}:00–{SESSION_END}:00 UTC
  Induló tőke       : ${INIT_CASH:,}
""")

    # ── Adatbetöltés – automatikus forrásválasztás ────────────────────────────
    vbt_freq = _VBT_FREQ.get(TIMEFRAME, "15min")
    csv_path = f"{SYMBOL}_{TIMEFRAME}.csv"
    df       = None

    print(f"Adatforrás keresése ({SYMBOL} {TIMEFRAME}, {YEARS_HISTORY} év)...")

    # 1) MT5 Python API -------------------------------------------------------
    try:
        print("  [1] MT5 Python API...")
        df = load_from_mt5(SYMBOL, TIMEFRAME, YEARS_HISTORY)
        print(f"  ✓ MT5 API: {len(df):,} gyertya")
    except ImportError:
        print("  ✗ MetaTrader5 nincs (pip install MetaTrader5)")
    except Exception as e:
        print(f"  ✗ MT5 API: {e}")

    # 2) tvdatafeed -----------------------------------------------------------
    if df is None:
        try:
            print("  [2] tvdatafeed (TradingView OANDA)...")
            df = load_from_tvdatafeed(SYMBOL, TIMEFRAME, YEARS_HISTORY)
            print(f"  ✓ tvdatafeed: {len(df):,} gyertya")
        except ImportError:
            print("  ✗ tvdatafeed nincs (pip install tvdatafeed)")
        except Exception as e:
            print(f"  ✗ tvdatafeed: {e}")

    # 3) Helyi CSV – több lehetséges fájlnevet próbálunk ----------------------
    if df is None:
        # Keresési prioritás: pontos név → pattern-alapú keresés
        candidate_csvs = [
            csv_path,                                        # XAUUSD_M15.csv
            f"{SYMBOL}_{TIMEFRAME.lower()}.csv",             # xauusd_m15.csv
        ]
        # Összes CSV a mappában, ami tartalmazza a symbol-t és a timeframe-et
        for pattern in [f"{SYMBOL}*{TIMEFRAME[1:]}*min*.csv",
                        f"{SYMBOL}*{TIMEFRAME}*.csv",
                        f"*{SYMBOL}*15*.csv",
                        f"*XAUUSD*.csv", f"*xauusd*.csv"]:
            candidate_csvs += glob.glob(os.path.join(os.path.dirname(
                os.path.abspath(__file__)), pattern))

        found_csv = None
        for path in dict.fromkeys(candidate_csvs):  # deduplicate, preserve order
            if os.path.exists(path):
                found_csv = path
                break

        if found_csv:
            try:
                print(f"  [3] Helyi CSV: {os.path.basename(found_csv)}")
                df = load_mt5_csv(found_csv)
                print(f"  ✓ CSV: {len(df):,} gyertya")
            except Exception as e:
                print(f"  ✗ CSV hiba: {e}")
        else:
            print(f"  ✗ CSV nem található ({csv_path})")
            print(f"       MT5 export: View → Symbols → {SYMBOL} → Bars → Export")

    # 4) Szintetikus fallback -------------------------------------------------
    if df is None:
        tf_mins = int(TIMEFRAME.replace("M", ""))
        print(f"  [4] Szintetikus adat ({YEARS_HISTORY} év, {TIMEFRAME})...")
        df = generate_synthetic_xauusd(years=YEARS_HISTORY, freq=f"{tf_mins}min")
        print(f"  ✓ Szintetikus: {len(df):,} gyertya")

    avg_atr = atr_series(df["High"], df["Low"], df["Close"], ATR_PERIOD).mean()
    print(f"\nBetöltve  : {len(df):,} gyertya  (teljes adatsor – indikátor warmup)")
    print(f"Időszak   : {df.index[0]} → {df.index[-1]}")
    print(f"Ár tartom.: ${df['Close'].min():.2f} – ${df['Close'].max():.2f}")
    print(f"Átlag ATR : ${avg_atr:.2f}")

    # ── HTF bias számítása a TELJES adatsoron (kauzális, nincs jövőszivárgás) ─
    section("HTF BIAS SZÁMÍTÁS – Teljes adatsoron (look-ahead FREE)")
    print("""
  A HTF indikátorok (D1 EMA50, W1 EMA10, H4 ADX, H4 EMA20) a TELJES
  2022–2025 adatsoron kerülnek kiszámításra, de minden bar csak a
  SAJÁT MÚLTBELI adatait használja (EWM adjust=False, ffill reindex).
  → Nincs jövőbeli szivárgás (no look-ahead bias).
""")
    htf_bias_full = compute_htf_bias(df)

    # ── Adatszűrés BACKTEST_YEAR-re (ha be van állítva) ───────────────────────
    if BACKTEST_YEAR is not None:
        section(f"BACKTEST SZŰRÉS: CSAK {BACKTEST_YEAR}")
        print(f"""
  Indikátorok : 2022-tól számítva (megfelelő warmup) → nincs look-ahead
  Portfólió   : Friss ${INIT_CASH:,} indul {BACKTEST_YEAR} jan. 1-jén
  FTMO költség: ${COMMISSION:.2f} USD/oz round-trip (spread + jutalék)
""")
        # Warmup: 30 bar × 15 perc = 7.5 óra swing high/low számításhoz
        warmup_start = pd.Timestamp(f"{BACKTEST_YEAR}-01-01") - pd.Timedelta(hours=8)
        df_warmup    = df[df.index >= warmup_start].copy()
        bias_warmup  = htf_bias_full.reindex(df_warmup.index, method="ffill").fillna(0)

        print(f"  Szignálok generálása ({BACKTEST_YEAR}, warmup: {warmup_start.date()} 00:00-tól)...")
        signals_warmup = generate_signals(df_warmup, htf_bias=bias_warmup)

        # Szűrés: csak a BACKTEST_YEAR-es kereskedések a portfólióba
        year_mask   = df_warmup.index.year == BACKTEST_YEAR
        df_bt       = df_warmup[year_mask].copy()
        signals_bt  = {k: v[year_mask] for k, v in signals_warmup.items()}
    else:
        print("\nSzignálok generálása (teljes adatsor)...")
        signals_bt = generate_signals(df, htf_bias=htf_bias_full)
        df_bt      = df

    n_long  = signals_bt["long_entries"].sum()
    n_short = signals_bt["short_entries"].sum()
    yr_lbl  = str(BACKTEST_YEAR) if BACKTEST_YEAR else "TELJES"
    print(f"\n  [{yr_lbl}] Long belépők  : {n_long}")
    print(f"  [{yr_lbl}] Short belépők : {n_short}")
    print(f"  [{yr_lbl}] Összes        : {n_long + n_short}")

    if (n_long + n_short) == 0:
        print("\nHIBA: Nincs egyetlen szignál sem a kiválasztott időszakban!")
        print("Próbálj kisebb MIN_BREAKOUT_PTS vagy nagyobb RETEST_ZONE_PCT értékkel.")
        exit(1)

    # ── Portfóliók építése ───────────────────────────────────────────────────
    pf_long  = build_portfolio(df_bt, signals_bt, "longonly")  if n_long  > 0 else None
    pf_short = build_portfolio(df_bt, signals_bt, "shortonly") if n_short > 0 else None

    # ── Statisztikák ─────────────────────────────────────────────────────────
    t_long  = print_portfolio_stats(pf_long,  f"LONG PORTFÓLIÓ – {yr_lbl}")  if pf_long  else None
    t_short = print_portfolio_stats(pf_short, f"SHORT PORTFÓLIÓ – {yr_lbl}") if pf_short else None

    # ── Havi bontás ──────────────────────────────────────────────────────────
    print_monthly_breakdown(t_long,  "Long")
    print_monthly_breakdown(t_short, "Short")
    combined_monthly_summary(t_long, t_short)

    # ── VectorBT teljes stats ────────────────────────────────────────────────
    section(f"VECTORBT RÉSZLETES STATS – LONG ({yr_lbl})")
    if pf_long:
        print(pf_long.stats().to_string())

    section(f"VECTORBT RÉSZLETES STATS – SHORT ({yr_lbl})")
    if pf_short:
        print(pf_short.stats().to_string())

    # ── FTMO Compliance összefoglaló ─────────────────────────────────────────
    section(f"FTMO COMPLIANCE ÖSSZEFOGLALÓ – {yr_lbl}")
    if pf_long:
        st      = pf_long.stats()
        dd      = float(st.get("Max Drawdown [%]", 0))
        fees    = float(st.get("Total Fees Paid", 0))
        ret     = (pf_long.final_value() / pf_long.init_cash - 1) * 100
        n_tr    = int(st.get("Total Closed Trades", 0))
        print(f"""
  ── Számla adatok ───────────────────────────────────────
  Induló tőke         : ${INIT_CASH:,}
  Végső érték         : ${pf_long.final_value():,.2f}
  Nettó hozam         : {ret:+.2f}%  (FTMO jutalék levonva)
  Bruttó hozam        : {(pf_long.final_value()+fees)/INIT_CASH*100-100:+.2f}%  (jutalék nélkül)

  ── FTMO Limitek ────────────────────────────────────────
  Max Drawdown        : {dd:.2f}%  {'✓ (limit: 10%)' if dd <= 10 else '✗ LIMIT TÚLLÉPVE!'}
  Napi veszteség limit: {FTMO_DAILY_LOSS_LIMIT*100:.1f}%  (kód-szintű védelemmel)
  FTMO max DD limit   : 10.0%

  ── Jutalék részletezés ─────────────────────────────────
  Jutalék (round-trip): ${COMMISSION:.2f} / oz  (FTMO XAUUSD)
  Összes kifizetett   : ${fees:,.2f}  ({fees/INIT_CASH*100:.2f}% of account)
  Jutalék / trade     : ${fees/n_tr:.2f}  (átlag, {n_tr} lezárt trade)
  Havi jutalék átlag  : ${fees/max(len(pd.period_range(df_bt.index[0], df_bt.index[-1], freq='M')),1):.2f}
""")

    print("\n[KÉSZ] Backtest befejezve.")
    print("       Valós MT5 adathoz: másold a XAUUSD_M15.csv-t ebbe a mappába.")
