#!/usr/bin/env python3
"""
NAS100 ORB + Regime Engine v2 — M5 Backtest (last 60 days)
Period  : ~2026-04-14 → 2026-06-13
Data    : ^NDX 5-minute bars (Yahoo Finance, max 60 days)
Account : $100,000 FTMO Swing simulation
"""

import yfinance as yf
import pandas as pd
import numpy as np
from datetime import date, timedelta
import warnings
warnings.filterwarnings("ignore")

# ══════════════════════════════════════════════════════
#  PARAMETERS
# ══════════════════════════════════════════════════════
CHALLENGE_BAL   = 100_000
RISK_PCT        = 1.0
MAX_DAILY_DD    = 4.5
MAX_TOTAL_DD    = 9.0

# ORB: 14:30–15:00 UTC (NY open 30-minute range on M5 bars)
ORB_START_UTC   = (14, 30)
ORB_MINS        = 30
ENTRY_DL_UTC    = 17
SESSION_END_UTC = 21

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

def atr_s(h, l, c, n):
    tr = pd.concat([h - l,
                    (h - c.shift(1)).abs(),
                    (l - c.shift(1)).abs()], axis=1).max(axis=1)
    return tr.ewm(span=n, adjust=False).mean()

def adx_s(h, l, c, n=14):
    tr   = pd.concat([h - l,
                      (h - c.shift(1)).abs(),
                      (l - c.shift(1)).abs()], axis=1).max(axis=1)
    up   = h.diff(); dn = -l.diff()
    dp   = pd.Series(np.where((up > dn) & (up > 0), up, 0.0), index=c.index)
    dm   = pd.Series(np.where((dn > up) & (dn > 0), dn, 0.0), index=c.index)
    tr_e = tr.ewm(span=n, adjust=False).mean()
    dp_e = dp.ewm(span=n, adjust=False).mean()
    dm_e = dm.ewm(span=n, adjust=False).mean()
    di_p = 100 * dp_e / tr_e.replace(0, np.nan)
    di_m = 100 * dm_e / tr_e.replace(0, np.nan)
    dx   = (100 * (di_p - di_m).abs() / (di_p + di_m).replace(0, np.nan)).fillna(0)
    return dx.ewm(span=n, adjust=False).mean()

def chop_s(h, l, c, n=14):
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
    if ts.dayofweek == 2 and 18 <= h < 21:                     return True  # FOMC
    if ts.dayofweek == 4 and ts.day <= 7 and 12 <= h < 15:     return True  # NFP
    if ts.dayofweek == 3 and 8 <= ts.day <= 14 and 12 <= h < 15: return True # CPI
    return False

# ══════════════════════════════════════════════════════
#  DATA
# ══════════════════════════════════════════════════════
END_DATE   = date.today()
START_DATE = END_DATE - timedelta(days=59)

print("═"*62)
print("  NAS100 ORB + Regime Engine v2 — M5 Backtest (60 nap)")
print(f"  Időszak: {START_DATE} → {END_DATE}")
print("═"*62)
print(f"\n  Letöltés: ^NDX 5m | {START_DATE} → {END_DATE} ...", end="", flush=True)

df = yf.download("^NDX", start=str(START_DATE), end=str(END_DATE),
                 interval="5m", progress=False, auto_adjust=True)

if isinstance(df.columns, pd.MultiIndex):
    df.columns = [c[0].lower() for c in df.columns]
else:
    df.columns = [c.lower() for c in df.columns]

if df.index.tz is None:
    df.index = df.index.tz_localize("UTC")
else:
    df.index = df.index.tz_convert("UTC")

print(f"  {len(df):,} bar")

# ══════════════════════════════════════════════════════
#  INDICATORS
# ══════════════════════════════════════════════════════
print("  Indikátorok számítása...")
df["ema200"] = ema(df["close"], EMA_N)
df["atr"]    = atr_s(df["high"], df["low"], df["close"], ATR_N)
df["adx"]    = adx_s(df["high"], df["low"], df["close"], ADX_N)
df["chop"]   = chop_s(df["high"], df["low"], df["close"], CHOP_N)
df["dhi"]    = df["high"].rolling(DONCH_N).max().shift(1)
df["dlo"]    = df["low"].rolling(DONCH_N).min().shift(1)
df.dropna(inplace=True)
print(f"  Bars (indikátor warmup után): {len(df):,}")
print(f"  Kereskedési napok: {df.index.normalize().nunique()}")
print(f"  Záróár átlag: {df['close'].mean():.0f}  |  ATR átlag: {df['atr'].mean():.1f} pts\n")

# ══════════════════════════════════════════════════════
#  BACKTEST
# ══════════════════════════════════════════════════════
bal = CHALLENGE_BAL; peak_bal = CHALLENGE_BAL; day_bal = CHALLENGE_BAL
cur_day = None; trades = []; daily_log = []
orb_h = None; orb_l = None; orb_ok = False
traded_today = False; cooldown = 0
pos = None

orb_s_min = ORB_START_UTC[0]*60 + ORB_START_UTC[1]
orb_e_min = orb_s_min + ORB_MINS

for row in df.itertuples():
    ts  = row.Index
    day = ts.date()
    t   = ts.hour * 60 + ts.minute

    # ── Daily reset ───────────────────────────────────────────────
    if day != cur_day:
        if pos is not None:
            ep  = row.close
            pl  = ((ep - pos["e"]) if pos["d"] == "L" else (pos["e"] - ep)) * pos["v"]
            bal += pl; peak_bal = max(peak_bal, bal)
            trades.append({**pos, "x": ep, "pnl": pl, "r": "day_end"})
            pos = None
        if cur_day is not None:
            daily_log.append({"date": cur_day,
                               "pnl": bal - day_bal,
                               "balance": bal})
        cur_day = day; day_bal = bal
        traded_today = False; orb_h = None; orb_l = None; orb_ok = False

    peak_bal = max(peak_bal, bal)
    if ts.weekday() >= 5: continue

    # ── Manage open position ──────────────────────────────────────
    if pos is not None:
        h = row.high; l = row.low; c = row.close

        # Break-even
        if not pos["be"]:
            profit = (c - pos["e"]) if pos["d"] == "L" else (pos["e"] - c)
            if profit >= pos["sd"] * BE_R:
                pos["sl"] = pos["e"]; pos["be"] = True

        # Trailing + Donchian exit (only after BE)
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

        # Session close
        if pos is not None and ts.hour >= SESSION_END_UTC:
            ep = row.close
            pl = ((ep - pos["e"]) if pos["d"] == "L" else (pos["e"] - ep)) * pos["v"]
            bal += pl; peak_bal = max(peak_bal, bal)
            trades.append({**pos, "x": ep, "pnl": pl, "r": "session_end"})
            pos = None; continue

    # ── ORB building (M5 bars within 14:30–15:00 UTC) ─────────────
    if orb_s_min <= t < orb_e_min:
        orb_h = max(orb_h or row.high, row.high)
        orb_l = min(orb_l or row.low,  row.low)
    elif t >= orb_e_min and not orb_ok and orb_h is not None:
        orb_ok = True

    # ── FTMO guards ───────────────────────────────────────────────
    daily_dd = (day_bal - bal) / max(day_bal, 1) * 100
    total_dd = (CHALLENGE_BAL - bal) / CHALLENGE_BAL * 100
    if daily_dd >= MAX_DAILY_DD or total_dd >= MAX_TOTAL_DD: continue

    if cooldown > 0: cooldown -= 1; continue

    # ── Entry gate ────────────────────────────────────────────────
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
    vol      = risk_amt / sl_dist

    pos = dict(d=d, e=c, sl=sl, sd=sl_dist, v=vol, be=False, date=day)
    traded_today = True

# ══════════════════════════════════════════════════════
#  REPORT
# ══════════════════════════════════════════════════════
if not trades:
    print("  Nincs trade. Ellenőrizd az adatot és a paramétereket.")
else:
    df_t = pd.DataFrame(trades)
    df_t["pnl_r"] = df_t["pnl"] / (df_t["sd"] * df_t["v"] + 1e-10)

    wins = df_t[df_t["pnl"] > 0]; loss = df_t[df_t["pnl"] <= 0]
    wr   = len(wins) / len(df_t) * 100
    avg_w = wins["pnl_r"].mean() if len(wins) > 0 else 0
    avg_l = loss["pnl_r"].mean() if len(loss) > 0 else 0
    exp   = (wr/100)*avg_w + (1-wr/100)*avg_l

    total_pnl = df_t["pnl"].sum()
    total_pct = total_pnl / CHALLENGE_BAL * 100
    days_cnt  = df.index.normalize().nunique()
    mon_equiv = total_pct / (days_cnt / 21.0)

    # Rolling DD
    running = CHALLENGE_BAL; pk = CHALLENGE_BAL; max_dd = 0.0
    for p in df_t["pnl"]:
        running += p; pk = max(pk, running)
        max_dd = max(max_dd, (pk - running) / pk * 100)

    # Weekly breakdown
    df_t["week"] = pd.to_datetime(df_t["date"]).dt.to_period("W")
    wk_pnl = df_t.groupby("week")["pnl"].sum()
    wk_cnt = df_t.groupby("week")["pnl"].count()

    # Daily equity curve
    df_d  = pd.DataFrame(daily_log)

    # Regime stats: count days with at least one trade vs skipped
    total_trading_days = df.index.normalize().nunique()
    trade_days = df_t["date"].nunique()

    print(f"\n{'═'*62}")
    print(f"  NAS100 ORB + Regime Engine v2 — M5 BACKTEST EREDMÉNYEK")
    print(f"  {START_DATE} → {END_DATE}  |  $100,000 FTMO Swing")
    print(f"  Granularitás: M5 (5 perces) — azonos az éles robottal!")
    print(f"{'═'*62}")
    print(f"  Kereskedési napok összesen : {total_trading_days}")
    print(f"  Napok trade-del            : {trade_days}  ({trade_days/total_trading_days*100:.0f}%)")
    print(f"  Napok kihagyva (regime/DD) : {total_trading_days - trade_days}")
    print(f"{'─'*62}")
    print(f"  Összes trade          : {len(df_t)}")
    print(f"    ↳ Long              : {len(df_t[df_t['d']=='L'])}  ({len(df_t[df_t['d']=='L'])/len(df_t)*100:.0f}%)")
    print(f"    ↳ Short             : {len(df_t[df_t['d']=='S'])}  ({len(df_t[df_t['d']=='S'])/len(df_t)*100:.0f}%)")
    print(f"  Win Rate              : {wr:.1f}%")
    print(f"  Átlag nyerő (R)       : +{avg_w:.2f}R  (${wins['pnl'].mean():,.0f})")
    print(f"  Átlag vesztes (R)     : {avg_l:.2f}R  (${loss['pnl'].mean():,.0f})")
    print(f"  Expectancy            : {exp:+.3f}R / trade")
    print(f"{'─'*62}")
    print(f"  Nettó P&L (60 nap)    : ${total_pnl:>+10,.0f}  ({total_pct:+.2f}%)")
    print(f"  Havi ekvivalens       : {mon_equiv:+.2f}%/hó")
    print(f"  Max Drawdown (roll.)  : {max_dd:.2f}%")
    print(f"  Záró Balance          : ${bal:>12,.0f}")
    print(f"  Peak Balance          : ${peak_bal:>12,.0f}")

    print(f"\n  Zárási okok:")
    for r, cnt in df_t["r"].value_counts().items():
        avg_p = df_t[df_t["r"]==r]["pnl"].mean()
        avg_r = df_t[df_t["r"]==r]["pnl_r"].mean()
        print(f"    {r:<16}: {cnt:3d} ({cnt/len(df_t)*100:.0f}%)  avg {avg_r:+.2f}R  ${avg_p:>+7,.0f}")

    # --- Napi részletes naplók ---
    print(f"\n  Napi trade napló:")
    print(f"  {'Dátum':<12} {'Irány':>5} {'Belépés':>8} {'Kilépés':>8} {'SL dist':>7} {'R':>5} {'P&L':>9} {'Zárás':<14}")
    print(f"  {'─'*12} {'─'*5} {'─'*8} {'─'*8} {'─'*7} {'─'*5} {'─'*9} {'─'*14}")
    for _, t in df_t.sort_values("date").iterrows():
        r_val = t["pnl_r"]
        pnl_s = f"${t['pnl']:>+8,.0f}"
        print(f"  {str(t['date']):<12} {t['d']:>5} {t['e']:>8.1f} {t['x']:>8.1f} "
              f"{t['sd']:>7.1f} {r_val:>+5.2f} {pnl_s}  {t['r']:<14}")

    print(f"\n  Heti bontás:")
    print(f"  {'Hét':<20} {'P&L':>10}  {'%':>6}  {'Tr':>3}  Chart")
    for wk in sorted(wk_pnl.index):
        p = wk_pnl[wk]; pct = p / CHALLENGE_BAL * 100; c = int(wk_cnt[wk])
        bar = ("▓" if p >= 0 else "░") * min(int(abs(pct)*5), 40)
        sgn = "+" if p >= 0 else ""
        print(f"  {str(wk):<20} ${p:>+9,.0f}  {sgn}{pct:>5.2f}%  {c:>3}  {bar}")

    print(f"\n  Indikátor átlagok (kereskedési időszak):")
    trading_bars = df[(df.index.hour >= 14) & (df.index.hour < 21)]
    print(f"    ADX átlag  : {trading_bars['adx'].mean():.1f}  (limit: >{ADX_THRESH})")
    print(f"    Chop átlag : {trading_bars['chop'].mean():.1f}  (limit: <{CHOP_THRESH})")
    regime_ok = ((trading_bars["adx"] > ADX_THRESH) & (trading_bars["chop"] < CHOP_THRESH)).mean() * 100
    print(f"    Trending rezsim arány: {regime_ok:.1f}% (a 14:00–21:00 UTC ablakban)")

    print(f"\n  FTMO Swing limit:")
    tot_dd = (CHALLENGE_BAL - bal) / CHALLENGE_BAL * 100
    print(f"    Max rolling DD (limit 10%) : {max_dd:.2f}%  {'✓' if max_dd < 10 else '✗'}")
    print(f"    Total DD tól (limit 10%)   : {tot_dd:+.2f}%  {'✓ (nyereség)' if tot_dd <= 0 else ('✓ OK' if tot_dd < 10 else '✗')}")

    print(f"{'═'*62}")
    print(f"\n  ⚠  Megjegyzések:")
    print(f"     • Ez M5 = az éles robottal AZONOS granularitás")
    print(f"     • 60 nap rövid minta — statisztikailag korlátozott")
    print(f"     • Slippage, swap, jutalék NEM szerepel")
    print(f"     • Összehasonlítás az 1H teszttel: pontosabb ORB detektálás")
