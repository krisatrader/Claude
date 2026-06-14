#!/usr/bin/env python3
"""
NAS100 ORB + Regime Engine v2 — Backtest Simulation
Period  : 2025-01-01 → 2026-06-13  (18 months)
Data    : ^NDX / QQQ  1-hour bars (Yahoo Finance, free & available for 730 days)
Note    : 1H bars approximate M5-based ORB — entry on the bar after NY open,
          SL/TP tracked on the same hourly granularity.
Account : $100,000 FTMO Swing Challenge simulation
"""

import yfinance as yf
import pandas as pd
import numpy as np
from datetime import date
import warnings
warnings.filterwarnings("ignore")

# ══════════════════════════════════════════════════════
#  PARAMETERS  (NAS100_ORB_RegimeEngine v2 defaults)
# ══════════════════════════════════════════════════════
CHALLENGE_BAL   = 100_000
RISK_PCT        = 1.0        # % per trade
MAX_DAILY_DD    = 4.5
MAX_TOTAL_DD    = 9.0

# On 1H bars the "ORB" bar is the one opening at 14:00 UTC
# (covers NY open 14:30–15:00 UTC within that bar)
ORB_HOUR_UTC    = 14         # Bar opening at 14:xx UTC = ORB bar
ENTRY_DL_UTC    = 17         # No new entries after 17:00 UTC
SESSION_END_UTC = 21         # Force-close at 21:00 UTC

ADX_N           = 14
ADX_THRESH      = 22.0
CHOP_N          = 14
CHOP_THRESH     = 55.0
EMA_N           = 200
ATR_N           = 14
SL_ATR_MULT     = 1.5
TRAIL_ATR_MULT  = 2.0
DONCH_N         = 10
BE_R            = 1.0
COOLDOWN_BARS   = 3

# ══════════════════════════════════════════════════════
#  INDICATORS
# ══════════════════════════════════════════════════════
def ema(s, n):
    return s.ewm(span=n, adjust=False).mean()

def atr_series(h, l, c, n):
    tr = pd.concat([h - l,
                    (h - c.shift(1)).abs(),
                    (l - c.shift(1)).abs()], axis=1).max(axis=1)
    return tr.ewm(span=n, adjust=False).mean()

def adx_series(h, l, c, n=14):
    tr   = pd.concat([h - l,
                      (h - c.shift(1)).abs(),
                      (l - c.shift(1)).abs()], axis=1).max(axis=1)
    up   = h.diff(); down = -l.diff()
    dp   = pd.Series(np.where((up > down) & (up > 0),   up,   0.0), index=c.index)
    dm   = pd.Series(np.where((down > up) & (down > 0), down, 0.0), index=c.index)
    tr_e  = tr.ewm(span=n, adjust=False).mean()
    dp_e  = dp.ewm(span=n, adjust=False).mean()
    dm_e  = dm.ewm(span=n, adjust=False).mean()
    di_p  = 100 * dp_e / tr_e.replace(0, np.nan)
    di_m  = 100 * dm_e / tr_e.replace(0, np.nan)
    dx    = (100 * (di_p - di_m).abs() / (di_p + di_m).replace(0, np.nan)).fillna(0)
    return dx.ewm(span=n, adjust=False).mean()

def chop_series(h, l, c, n=14):
    tr      = pd.concat([h - l,
                         (h - c.shift(1)).abs(),
                         (l - c.shift(1)).abs()], axis=1).max(axis=1)
    atr_sum = tr.rolling(n).sum()
    span    = (h.rolling(n).max() - l.rolling(n).min()).replace(0, np.nan)
    return (100 * np.log10(atr_sum / span) / np.log10(n)).fillna(61.8)

# ══════════════════════════════════════════════════════
#  NEWS FILTER
# ══════════════════════════════════════════════════════
def is_news_blocked(ts):
    h = ts.hour
    if ts.dayofweek == 2 and 18 <= h < 21:          return True   # FOMC Wed
    if ts.dayofweek == 4 and ts.day <= 7 and 12 <= h < 15: return True  # NFP 1st Fri
    if ts.dayofweek == 3 and 8 <= ts.day <= 14 and 12 <= h < 15: return True  # CPI 2nd Thu
    return False

# ══════════════════════════════════════════════════════
#  DATA DOWNLOAD
# ══════════════════════════════════════════════════════
def download_1h(ticker, start, end):
    print(f"  Letöltés: {ticker} 1H | {start} → {end} ...", end="", flush=True)
    try:
        df = yf.download(ticker, start=start, end=end,
                         interval="1h", progress=False, auto_adjust=True)
        if isinstance(df.columns, pd.MultiIndex):
            df.columns = [c[0].lower() for c in df.columns]
        else:
            df.columns = [c.lower() for c in df.columns]
        if df.index.tz is None:
            df.index = df.index.tz_localize("UTC")
        else:
            df.index = df.index.tz_convert("UTC")
        print(f"  {len(df):,} bars")
        return df
    except Exception as e:
        print(f"  HIBA: {e}")
        return pd.DataFrame()

# ══════════════════════════════════════════════════════
#  BACKTEST ENGINE
# ══════════════════════════════════════════════════════
def run_backtest(df_raw):
    df = df_raw.copy()
    print("  Indikátorok...")
    df["ema200"] = ema(df["close"], EMA_N)
    df["atr"]    = atr_series(df["high"], df["low"], df["close"], ATR_N)
    df["adx"]    = adx_series(df["high"], df["low"], df["close"], ADX_N)
    df["chop"]   = chop_series(df["high"], df["low"], df["close"], CHOP_N)
    df["dhi"]    = df["high"].rolling(DONCH_N).max().shift(1)
    df["dlo"]    = df["low"].rolling(DONCH_N).min().shift(1)
    df.dropna(inplace=True)
    print(f"  Felhasználható bar: {len(df):,}")

    bal = CHALLENGE_BAL; peak_bal = CHALLENGE_BAL; day_bal = CHALLENGE_BAL
    cur_day = None; trades = []
    orb_h = None; orb_l = None; orb_ok = False
    traded_today = False; cooldown = 0
    pos = None

    for row in df.itertuples():
        ts  = row.Index
        day = ts.date()

        # ── Daily reset ───────────────────────────────────────────
        if day != cur_day:
            if pos is not None:
                ep = row.close
                pl = ((ep - pos["e"]) if pos["d"] == "L" else (pos["e"] - ep)) * pos["v"]
                bal += pl; peak_bal = max(peak_bal, bal)
                trades.append({**pos, "x": ep, "pnl": pl, "r": "day_end"})
                pos = None
            cur_day = day; day_bal = bal
            traded_today = False; orb_h = None; orb_l = None; orb_ok = False
        peak_bal = max(peak_bal, bal)

        if ts.weekday() >= 5: continue

        # ── Manage position ──────────────────────────────────────
        if pos is not None:
            h = row.high; l = row.low; c = row.close

            # Break-even check
            if not pos["be"]:
                profit = (c - pos["e"]) if pos["d"] == "L" else (pos["e"] - c)
                if profit >= pos["sd"] * BE_R:
                    pos["sl"] = pos["e"]
                    pos["be"] = True

            # ATR trailing (after BE)
            if pos["be"]:
                trail = row.atr * TRAIL_ATR_MULT
                if pos["d"] == "L":
                    pos["sl"] = max(pos["sl"], c - trail)
                    if c < row.dlo:
                        ep = c; pl = (ep - pos["e"]) * pos["v"]
                        bal += pl; peak_bal = max(peak_bal, bal)
                        trades.append({**pos, "x": ep, "pnl": pl, "r": "donchian"})
                        pos = None; cooldown = 0 if pl > 0 else COOLDOWN_BARS; continue
                else:
                    pos["sl"] = min(pos["sl"], c + trail)
                    if c > row.dhi:
                        ep = c; pl = (pos["e"] - ep) * pos["v"]
                        bal += pl; peak_bal = max(peak_bal, bal)
                        trades.append({**pos, "x": ep, "pnl": pl, "r": "donchian"})
                        pos = None; cooldown = 0 if pl > 0 else COOLDOWN_BARS; continue

            # SL hit
            if pos is not None:
                sl_hit = (pos["d"] == "L" and l <= pos["sl"]) or \
                         (pos["d"] == "S" and h >= pos["sl"])
                if sl_hit:
                    ep = pos["sl"]
                    pl = ((ep - pos["e"]) if pos["d"] == "L" else (pos["e"] - ep)) * pos["v"]
                    bal += pl; peak_bal = max(peak_bal, bal)
                    trades.append({**pos, "x": ep, "pnl": pl, "r": "sl"})
                    pos = None; cooldown = COOLDOWN_BARS if pl < 0 else 0; continue

            # Session end
            if pos is not None and ts.hour >= SESSION_END_UTC:
                ep = row.close
                pl = ((ep - pos["e"]) if pos["d"] == "L" else (pos["e"] - ep)) * pos["v"]
                bal += pl; peak_bal = max(peak_bal, bal)
                trades.append({**pos, "x": ep, "pnl": pl, "r": "session_end"})
                pos = None; continue

        # ── ORB bar (14:00 UTC = first hour of NY session) ────────
        if ts.hour == ORB_HOUR_UTC:
            orb_h = row.high
            orb_l = row.low
        elif ts.hour > ORB_HOUR_UTC and not orb_ok and orb_h is not None:
            orb_ok = True

        # ── FTMO guards ───────────────────────────────────────────
        daily_dd = (day_bal - bal) / max(day_bal, 1) * 100
        total_dd = (CHALLENGE_BAL - bal) / CHALLENGE_BAL * 100
        if daily_dd >= MAX_DAILY_DD or total_dd >= MAX_TOTAL_DD: continue

        # ── Cooldown ──────────────────────────────────────────────
        if cooldown > 0:
            cooldown -= 1; continue

        # ── Entry gate ────────────────────────────────────────────
        if not orb_ok or traded_today or pos or ts.hour >= ENTRY_DL_UTC: continue
        if is_news_blocked(ts): continue

        # Regime
        if not (row.adx > ADX_THRESH and row.chop < CHOP_THRESH): continue

        c    = row.close
        bull = c > row.ema200
        bear = c < row.ema200
        atrv = row.atr

        if bull and c > orb_h:
            d = "L"; sl_dist = min(c - orb_l, atrv * SL_ATR_MULT); sl = c - sl_dist
        elif bear and c < orb_l:
            d = "S"; sl_dist = min(orb_h - c, atrv * SL_ATR_MULT); sl = c + sl_dist
        else:
            continue

        if sl_dist <= 0 or sl_dist > c * 0.04: continue

        risk_amt = bal * (RISK_PCT / 100)
        vol      = risk_amt / sl_dist  # units such that 1pt = $1/unit

        pos = dict(d=d, e=c, sl=sl, sd=sl_dist, v=vol, be=False, date=day)
        traded_today = True

    return trades, bal, peak_bal

# ══════════════════════════════════════════════════════
#  REPORT
# ══════════════════════════════════════════════════════
def report(trades, final_bal, peak_bal):
    if not trades:
        print("\n  Nincs egyetlen trade sem."); return

    df = pd.DataFrame(trades)
    df["pnl_r"] = df["pnl"] / (df["sd"] * df["v"] + 1e-10)

    wins = df[df["pnl"] > 0]; loss = df[df["pnl"] <= 0]
    wr   = len(wins) / len(df) * 100
    avg_w = wins["pnl_r"].mean() if len(wins) > 0 else 0
    avg_l = loss["pnl_r"].mean() if len(loss) > 0 else 0
    exp   = (wr/100)*avg_w + (1 - wr/100)*avg_l

    total_pnl = df["pnl"].sum()
    total_pct = total_pnl / CHALLENGE_BAL * 100
    months    = 18.4
    mon_pl    = total_pnl / months
    mon_pc    = total_pct / months

    # Max rolling DD
    running = CHALLENGE_BAL; pk = CHALLENGE_BAL; max_dd = 0.0
    for p in df["pnl"]:
        running += p; pk = max(pk, running)
        max_dd = max(max_dd, (pk - running) / pk * 100)

    # Monthly
    df["month"] = pd.to_datetime(df["date"]).dt.to_period("M")
    mon_pnl = df.groupby("month")["pnl"].sum()
    mon_pct = mon_pnl / CHALLENGE_BAL * 100
    mon_cnt = df.groupby("month")["pnl"].count()

    # Long vs Short
    longs  = df[df["d"] == "L"]; shorts = df[df["d"] == "S"]

    print(f"\n{'═'*60}")
    print(f"  NAS100 ORB + Regime Engine v2 — BACKTEST EREDMÉNYEK")
    print(f"  Időszak : 2025-01-01 → 2026-06-13 (~18 hónap)")
    print(f"  Szimulált tőke : $100,000 FTMO Swing")
    print(f"  Adatok : ^NDX 1H bars  |  Kockázat: 1%/trade")
    print(f"{'═'*60}")
    print(f"  Összes trade          : {len(df)}")
    print(f"    ↳ Long              : {len(longs)}  ({len(longs)/len(df)*100:.0f}%)")
    print(f"    ↳ Short             : {len(shorts)} ({len(shorts)/len(df)*100:.0f}%)")
    print(f"  Win Rate              : {wr:.1f}%")
    print(f"  Átlag nyerő (R)       : +{avg_w:.2f}R")
    print(f"  Átlag vesztes (R)     : {avg_l:.2f}R")
    print(f"  Expectancy            : {exp:+.3f}R / trade")
    print(f"{'─'*60}")
    print(f"  Nettó P&L             : ${total_pnl:>+10,.0f}  ({total_pct:+.1f}%)")
    print(f"  Havi átlag            : ${mon_pl:>+10,.0f}  ({mon_pc:+.2f}%/hó)")
    print(f"  Max Drawdown (rolling): {max_dd:.2f}%")
    print(f"  Záró Balance          : ${final_bal:>12,.0f}")
    print(f"  Peak Balance          : ${peak_bal:>12,.0f}")

    print(f"\n  Zárási okok:")
    for r, c in df["r"].value_counts().items():
        pnl_r = df[df["r"]==r]["pnl"].mean()
        print(f"    {r:<16}: {c:3d}  ({c/len(df)*100:.0f}%)  avg P&L ${pnl_r:+,.0f}")

    print(f"\n  Havi bontás  (% of $100k):")
    print(f"  {'Hónap':<10} {'%':>7}  {'Tr':>3}  Chart")
    for m in sorted(mon_pct.index):
        pct = mon_pct[m]; cnt = int(mon_cnt.get(m, 0))
        bar = ("▓" if pct >= 0 else "░") * min(int(abs(pct) * 3), 40)
        sgn = "+" if pct >= 0 else ""
        print(f"  {str(m):<10} {sgn}{pct:>5.2f}%  {cnt:>3}  {bar}")

    print(f"\n  FTMO Swing limit ellenőrzés:")
    tot_dd = (CHALLENGE_BAL - final_bal) / CHALLENGE_BAL * 100
    print(f"    Max rolling DD (limit 10%)  : {max_dd:.2f}%  {'✓ OK' if max_dd < 10 else '✗ LIMIT ÁTLÉPVE'}")
    print(f"    Total DD (limit 10%)        : {tot_dd:.2f}%  {'✓ OK' if tot_dd < 10 else '✗ LIMIT ÁTLÉPVE'}")
    if max_dd < 5:
        print(f"    Napi DD becsült max (~half) : {max_dd/2:.2f}%  ✓ OK")
    print(f"{'═'*60}")
    print(f"\n  ⚠  Megjegyzések:")
    print(f"     • 1H bars = kevésbé precíz, mint M5 (ORB aprox.)")
    print(f"     • Valós M5-ös backtest pontosabb eredményt ad")
    print(f"     • Slippage, swap, jutalék NEM szerepel a P&L-ben")
    print(f"     • A szimulált eredmények NEM garantálják a jövőt")


# ══════════════════════════════════════════════════════
#  MAIN
# ══════════════════════════════════════════════════════
print("═"*60)
print("  NAS100 ORB + Regime Engine v2 — Backtest indítása")
print("  ADX>22, CI<55, EMA200, 30-min ORB | FTMO Swing $100k")
print("═"*60)

# Try ^NDX, fall back to QQQ
df = download_1h("^NDX", "2025-01-01", "2026-06-14")
if len(df) < 500:
    print("  ^NDX korlátozott, QQQ próba...")
    df = download_1h("QQQ", "2025-01-01", "2026-06-14")
    # Scale QQQ → NDX approx (QQQ ~= NDX/40)
    if len(df) > 0:
        print("  QQQ→NDX skálázás (~×40)...")
        for col in ["open","high","low","close"]:
            df[col] = df[col] * 40.0

if len(df) == 0:
    print("Letöltés sikertelen.")
else:
    # Filter to trading period
    df = df[df.index >= "2025-01-01"]
    print(f"\n  Bars: {len(df):,}  |  {df.index[0].date()} → {df.index[-1].date()}")
    print(f"  Kereskedési napok: ~{df.index.normalize().nunique()}")
    print(f"  Átlag napi záróár: ~{df['close'].mean():.0f}\n")

    trades, final_bal, peak_bal = run_backtest(df)
    report(trades, final_bal, peak_bal)
