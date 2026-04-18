"""
Aktív stratégia backtest – 2025
Cél: ~15-20 kötés/hónap

Változások az alap stratégiához képest:
  1. HTF filter: W1 EMA10 + H4 EMA20 + H4 ADX (D1 slope elhagyva → több bull idő)
  2. Swing szintek: [5, 8, 12, 15, 20, 25] (6 párhuzamos gép)
  3. Egyidejű pozíciók: max 2 (2 slot párhuzamosan fut)
  4. R:R = 3.0 (SL=0.8%, TP=2.4%) – gyorsabb pozíciólezárás
  5. BE trigger: 1.5×SL
  6. Max trades/nap: 4
"""
import sys, os, types, numpy as np, pandas as pd
import glob as _glob

src_path = os.path.join(os.path.dirname(__file__), "xauusd_vectorbt_test.py")
with open(src_path) as f:
    src = f.read()
main_idx = src.index('\nif __name__ == "__main__":')
exec(compile(src[:main_idx], src_path, "exec"))

# ── Adatbetöltés ──────────────────────────────────────────────────────────────
csv_files = _glob.glob(os.path.join(os.path.dirname(__file__), "*XAUUSD*15*.csv")) + \
            _glob.glob(os.path.join(os.path.dirname(__file__), "*xauusd*.csv"))
df = None
for cp in dict.fromkeys(csv_files):
    if os.path.exists(cp):
        try:
            df = load_mt5_csv(cp)
            print(f"CSV: {os.path.basename(cp)} ({len(df):,} gyertya)")
            break
        except:
            pass
if df is None:
    df = generate_synthetic_xauusd(years=4, freq="15min")

# ── Aktív HTF bias: W1 EMA10 + H4 EMA20 + H4 ADX≥20 (D1 slope nélkül) ───────
def compute_active_htf_bias(df_full):
    bias = pd.Series(0, index=df_full.index, dtype=int)

    # W1: ár > EMA10 (shift(1) a Python-hoz igazítva)
    w1 = df_full["Close"].resample("W").last().dropna()
    w1_ema = w1.ewm(span=10, adjust=False).mean()
    w1_bull = (w1 > w1_ema.shift(1)).astype(int).replace(0, -1)
    w1_m15 = w1_bull.reindex(df_full.index, method="ffill").fillna(0)
    # W1 momentum
    w1_mom = (w1 > w1.shift(1)).astype(int)
    w1_mom_m15 = w1_mom.reindex(df_full.index, method="ffill").fillna(0)

    # H4: EMA20 + ADX≥20
    h4 = df_full.resample("4h").agg({"High": "max", "Low": "min", "Close": "last"}).dropna()
    h4_adx = adx_series(h4["High"], h4["Low"], h4["Close"], 14)
    h4_ema20 = h4["Close"].ewm(span=20, adjust=False).mean()
    adx_m15 = h4_adx.reindex(df_full.index, method="ffill").fillna(0)
    h4_bull_m15 = (h4["Close"] > h4_ema20).astype(int).reindex(df_full.index, method="ffill").fillna(0)

    trending = adx_m15 >= 20
    bull_cond = trending & (w1_m15 == 1) & (w1_mom_m15 == 1) & (h4_bull_m15 == 1)
    bias[bull_cond] = 1
    return bias

print("HTF bias számítás (W1+H4)...")
htf_active = compute_active_htf_bias(df)

# ── Aktív stratégia paraméterei ───────────────────────────────────────────────
ACTIVE_SWING_LEVELS   = [5, 8, 12, 15, 20, 25]
ACTIVE_RR             = 3.0      # SL=0.8%, TP=2.4%
ACTIVE_SL_PCT         = TRAIL_STOP_PCT   # 0.008 = 0.8%
ACTIVE_TP_PCT         = ACTIVE_SL_PCT * ACTIVE_RR
ACTIVE_BE_RR          = 1.5      # BE trigger: 1.5×SL
ACTIVE_MAX_TRADES_DAY = 4        # max 4 kötés/nap (cél: átlag ~1/nap)
MAX_CONCURRENT        = 2        # max 2 párhuzamos nyitott pozíció

# ── Aktív szignál generálás ───────────────────────────────────────────────────
def generate_active_signals(df_sub, htf_bias_sub):
    """
    Generates entry signals for the active strategy.
    Supports MAX_CONCURRENT=2 simultaneous open positions via 2 independent slots.
    Returns a list of signal dicts (one per potential entry bar).
    """
    n = len(df_sub)
    idx = df_sub.index
    close = df_sub["Close"].values
    open_ = df_sub["Open"].values
    high  = df_sub["High"].values
    low   = df_sub["Low"].values
    bias_v = htf_bias_sub.values
    hours = idx.hour

    swing_highs = [df_sub["High"].shift(2).rolling(lb).max().values for lb in ACTIVE_SWING_LEVELS]
    n_lvl = len(ACTIVE_SWING_LEVELS)

    bo_dir   = [0]     * n_lvl
    bo_level = [0.0]   * n_lvl
    bo_atr_l = [0.0]   * n_lvl
    rt_count = [0]     * n_lvl
    waiting  = [False] * n_lvl

    signals = []  # list of (bar_idx, entry info)

    last_day     = None
    trades_today = 0

    for i in range(max(ACTIVE_SWING_LEVELS) + 5, n - 1):
        curr_day = idx[i].date()
        if curr_day != last_day:
            trades_today = 0
            last_day = curr_day

        if trades_today >= ACTIVE_MAX_TRADES_DAY:
            continue
        if not (7 <= int(hours[i]) < 18):
            continue
        if bias_v[i] != 1:
            continue

        p_c = close[i-1]; p_o = open_[i-1]; p_h = high[i-1]; p_l = low[i-1]
        c_c = close[i]

        entry_taken = False
        for lvl in range(n_lvl):
            if entry_taken:
                break
            sh = swing_highs[lvl][i]
            if np.isnan(sh):
                continue

            if not waiting[lvl]:
                if bias_v[i] == 1 and p_c > sh + MIN_BREAKOUT_PTS:
                    bo_dir[lvl] = 1; bo_level[lvl] = sh
                    atr_v_i = atr_series(df_sub["High"], df_sub["Low"], df_sub["Close"], 14).values[i]
                    bo_atr_l[lvl] = atr_v_i if not np.isnan(atr_v_i) else 0
                    rt_count[lvl] = 0; waiting[lvl] = True
            else:
                if bias_v[i] != 1:
                    waiting[lvl] = False; bo_dir[lvl] = 0; continue
                rt_count[lvl] += 1
                if rt_count[lvl] > MAX_RETEST_CANDLES:
                    waiting[lvl] = False; bo_dir[lvl] = 0; continue

                zone = bo_level[lvl] * (RETEST_ZONE_PCT / 100)
                touched = (p_l <= bo_level[lvl] + zone)
                bullish = (p_c > p_o and (p_c - p_o) > 0.25 * (p_h - p_l + 1e-9))
                if touched and bullish:
                    sl_d = c_c * ACTIVE_SL_PCT
                    if sl_d / c_c > MAX_SL_PCT:
                        waiting[lvl] = False; bo_dir[lvl] = 0; continue
                    signals.append({
                        "bar": i,
                        "entry": c_c,
                        "sl_d":  sl_d,
                        "tp_d":  c_c * ACTIVE_TP_PCT,
                        "size":  min(INIT_CASH * LONG_RISK_PCT / 100.0 / sl_d, 35.0),
                    })
                    trades_today += 1
                    waiting[lvl] = False; bo_dir[lvl] = 0
                    entry_taken = True

    return signals

# ── Multi-slot szimulátor ─────────────────────────────────────────────────────
def simulate_active(df_sub, signals_list, be_rr=1.5, max_concurrent=2):
    """
    Szimulálja a kereskedést MAX_CONCURRENT párhuzamos pozícióval.
    Slot-alapú: ha szabad slot van, benyitjuk a következő szignált.
    """
    close_a = df_sub["Close"].values
    high_a  = df_sub["High"].values
    low_a   = df_sub["Low"].values
    idx     = df_sub.index

    # Rendezzük a szignálokat bar-index szerint
    sig_by_bar = {}
    for s in signals_list:
        sig_by_bar.setdefault(s["bar"], []).append(s)

    # Slotok: aktív kereskedések listája
    active = []   # list of dicts representing open positions
    closed = []   # list of dicts representing closed positions

    equity    = float(INIT_CASH)
    peak_eq   = equity
    max_dd    = 0.0

    for i in range(len(df_sub)):
        h = float(high_a[i])
        l = float(low_a[i])

        # 1) Kezeljük a nyitott pozíciókat
        still_active = []
        for pos in active:
            ep      = pos["entry"]
            sl_d    = pos["sl_d"]
            tp_d    = pos["tp_d"]
            sz      = pos["sz"]
            sl      = pos["sl"]
            tp      = pos["tp"]
            be_done = pos["be_done"]
            trail_h = pos["trail_h"]
            be_t    = ep + sl_d * be_rr

            # BE trigger
            if not be_done and h >= be_t:
                be_done    = True
                sl         = ep          # nullra hozzuk
                trail_h    = h
                pos["be_done"] = True
                pos["trail_h"] = trail_h

            # Trailing after BE
            if be_done:
                if h > trail_h:
                    trail_h = h
                    pos["trail_h"] = trail_h
                new_tsl = trail_h * (1.0 - ACTIVE_SL_PCT)
                if new_tsl > sl:
                    sl = new_tsl
                    pos["sl"] = sl

            # SL ellenőrzés
            if l <= sl:
                exit_p = max(sl, l)
                exit_t = "SL-BE" if be_done else "SL"
                pnl    = (exit_p - ep) * sz - COMMISSION * sz
                equity += pnl
                closed.append({
                    "Entry Time":   idx[pos["bar"]],
                    "Entry Price":  round(ep, 2),
                    "Exit Time":    idx[i],
                    "Exit Price":   round(exit_p, 2),
                    "Exit Type":    exit_t,
                    "Size (oz)":    round(sz, 3),
                    "PnL ($)":      round(pnl, 2),
                    "BE Triggered": be_done,
                    "Fees ($)":     round(COMMISSION * sz, 2),
                })
                continue

            # TP ellenőrzés
            if h >= tp:
                pnl = (tp - ep) * sz - COMMISSION * sz
                equity += pnl
                closed.append({
                    "Entry Time":   idx[pos["bar"]],
                    "Entry Price":  round(ep, 2),
                    "Exit Time":    idx[i],
                    "Exit Price":   round(tp, 2),
                    "Exit Type":    "TP",
                    "Size (oz)":    round(sz, 3),
                    "PnL ($)":      round(pnl, 2),
                    "BE Triggered": be_done,
                    "Fees ($)":     round(COMMISSION * sz, 2),
                })
                continue

            pos["sl"] = sl
            still_active.append(pos)

        active = still_active
        if equity > peak_eq:
            peak_eq = equity
        dd = (peak_eq - equity) / peak_eq * 100
        if dd > max_dd:
            max_dd = dd

        # 2) Nyissunk új pozíciót ha van szabad slot és szignál
        if i in sig_by_bar and len(active) < max_concurrent:
            for sig in sig_by_bar[i]:
                if len(active) >= max_concurrent:
                    break
                ep   = sig["entry"]
                sl_d = sig["sl_d"]
                sz   = sig["size"]
                # FTMO max DD check
                est_loss = sz * sl_d
                if (equity - est_loss) / INIT_CASH < (1.0 - FTMO_MAX_DD_LIMIT):
                    continue
                active.append({
                    "bar":     i,
                    "entry":   ep,
                    "sl_d":    sl_d,
                    "tp_d":    sig["tp_d"],
                    "sz":      sz,
                    "sl":      ep - sl_d,
                    "tp":      ep + sig["tp_d"],
                    "be_done": False,
                    "trail_h": ep,
                })

    # Zárjuk a maradék pozíciókat az utolsó áron
    last_price = float(close_a[-1])
    for pos in active:
        ep  = pos["entry"]
        sz  = pos["sz"]
        pnl = (last_price - ep) * sz - COMMISSION * sz
        equity += pnl
        closed.append({
            "Entry Time":   idx[pos["bar"]],
            "Entry Price":  round(ep, 2),
            "Exit Time":    idx[-1],
            "Exit Price":   round(last_price, 2),
            "Exit Type":    "OPEN",
            "Size (oz)":    round(sz, 3),
            "PnL ($)":      round(pnl, 2),
            "BE Triggered": pos["be_done"],
            "Fees ($)":     round(COMMISSION * sz, 2),
        })

    result_df = pd.DataFrame(closed)
    result_df["_final_equity"] = equity
    result_df["_max_dd"] = max_dd
    result_df["_peak_eq"] = peak_eq
    return result_df

# ── Futtatás 2025-re ──────────────────────────────────────────────────────────
print("\nSzignálok generálása (W1+H4 HTF, 6 swing szint)...")
warmup_start = pd.Timestamp("2024-12-31 16:00")
df_w  = df[df.index >= warmup_start].copy()
bias_w = htf_active.reindex(df_w.index, method="ffill").fillna(0)
year_mask = df_w.index.year == 2025
df_bt = df_w[year_mask].copy()
bias_bt = bias_w[year_mask]

raw_sigs = generate_active_signals(df_bt, bias_bt)
n_raw = len(raw_sigs)
print(f"  Nyers szignálok (2025): {n_raw}  ({n_raw/11:.1f}/hó)")

print(f"\nSzimuláció: BE {ACTIVE_BE_RR}×SL + Trail | RR={ACTIVE_RR}:1 | max {MAX_CONCURRENT} egyidejű pozíció...")
result = simulate_active(df_bt, raw_sigs, be_rr=ACTIVE_BE_RR, max_concurrent=MAX_CONCURRENT)

closed = result[result["Exit Type"] != "OPEN"].copy()
n      = len(closed)
final_eq  = result["_final_equity"].iloc[-1] if len(result) > 0 else INIT_CASH
max_dd    = result["_max_dd"].iloc[-1] if len(result) > 0 else 0.0

if n == 0:
    print("Nincs lezárt trade.")
else:
    wr        = (closed["PnL ($)"] > 0).mean() * 100
    total_pnl = closed["PnL ($)"].sum()
    fees      = closed["Fees ($)"].sum()
    tp_ex  = closed[closed["Exit Type"] == "TP"]
    be_ex  = closed[closed["Exit Type"] == "SL-BE"]
    sl_ex  = closed[closed["Exit Type"] == "SL"]
    be_tri = closed[closed["BE Triggered"] == True]

    closed["Month"] = pd.to_datetime(closed["Entry Time"]).dt.to_period("M")
    monthly = closed.groupby("Month").agg(
        Kötés    = ("PnL ($)", "count"),
        Win_Rate = ("PnL ($)", lambda x: f"{(x > 0).mean()*100:.0f}%"),
        PnL_USD  = ("PnL ($)", lambda x: f"${x.sum():+.2f}"),
        PnL_Pct  = ("PnL ($)", lambda x: f"{x.sum()/INIT_CASH*100:+.2f}%"),
    )
    n_months  = closed["Month"].nunique()
    avg_m_pct = total_pnl / n_months / INIT_CASH * 100 if n_months > 0 else 0

    print(f"""
══════════════════════════════════════════════════════════
  AKTÍV STRATÉGIA EREDMÉNY – 2025
  HTF: W1+H4 | Swing: [5,8,12,15,20,25] | RR={ACTIVE_RR}:1
  BE: {ACTIVE_BE_RR}×SL + Trail | Max {MAX_CONCURRENT} párhuzamos pozíció
══════════════════════════════════════════════════════════
  Lezárt kötések      : {n}   ({n/n_months:.1f}/hó átlag)
  Win Rate            : {wr:.1f}%
  Összes PnL (nettó)  : ${total_pnl:+,.2f}
  FTMO jutalék össz.  : ${fees:,.2f}
  Végső tőke          : ${final_eq:,.2f}
  Hozam               : {(final_eq/INIT_CASH - 1)*100:+.2f}%
  Max Drawdown        : {max_dd:.2f}%  {'✓ FTMO OK' if max_dd <= 10 else '✗ LIMIT!'}
  Átlag havi hozam    : {avg_m_pct:+.2f}%
  Havi 5% cél         : {'✓ Elért' if avg_m_pct >= 5.0 else f'✗ Hiány: {5.0-avg_m_pct:.2f}%'}

  ── Exit típusok ────────────────────────────────────────
  TP   (profit célár) : {len(tp_ex):>3} db  ({len(tp_ex)/n*100:.1f}%)   ${tp_ex['PnL ($)'].sum():+,.2f}
  SL-BE (BE védte)    : {len(be_ex):>3} db  ({len(be_ex)/n*100:.1f}%)   ${be_ex['PnL ($)'].sum():+,.2f}
  SL   (hagyományos)  : {len(sl_ex):>3} db  ({len(sl_ex)/n*100:.1f}%)   ${sl_ex['PnL ($)'].sum():+,.2f}
  BE trigger aktív    : {len(be_tri):>3} trade ({len(be_tri)/n*100:.1f}%)
""")
    print("  Havi bontás:")
    print("  " + "─"*56)
    print(monthly.to_string())

    # ── Összehasonlítás ──────────────────────────────────────────────────────
    print(f"""
══════════════════════════════════════════════════════════
  ÖSSZEHASONLÍTÁS: Konzervatív vs Aktív
══════════════════════════════════════════════════════════
  {'Mutató':<28} {'Konzervatív':>12}  {'Aktív':>12}
  {'─'*55}
  Kötés/hónap              {41/11:>11.1f}   {n/n_months:>11.1f}
  Win Rate                    61.0%         {wr:.1f}%
  Átlag havi hozam            +4.12%        {avg_m_pct:>+10.2f}%
  Összes hozam                +45.34%       {(final_eq/INIT_CASH-1)*100:>+9.2f}%
  Max Drawdown                 2.72%         {max_dd:>10.2f}%
  RR                           4.0:1          {ACTIVE_RR:.1f}:1
  HTF rétegek                  5              3
  {'─'*55}""")
    print(f"  Szignál/hónap (nyers):  {41*100/100:.0f} (becsült)   {n_raw/11:.1f}")
