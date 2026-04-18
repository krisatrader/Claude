"""
Ideiglenes runner: MAX_TRADES_PER_DAY=1 backtest 2025-re
BE 2.0×SL + Trail scenario
"""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))

# ── Import the main module's functions WITHOUT running __main__ ──────────────
import importlib.util, types

src_path = os.path.join(os.path.dirname(__file__), "xauusd_vectorbt_test.py")
with open(src_path) as f:
    src = f.read()

# Create module in a sandboxed namespace
mod = types.ModuleType("bt_1trade")
mod.__file__ = src_path
mod.__spec__ = None

# Override MAX_TRADES_PER_DAY before exec
src_patched = src.replace(
    "MAX_TRADES_PER_DAY  = 2       # Max kötés naponta",
    "MAX_TRADES_PER_DAY  = 1       # Max kötés naponta  [OVERRIDE: 1-trade version]"
)

# Remove __main__ guard so we can re-use functions only
# Execute up to (but not including) the if __name__ block
main_guard_idx = src_patched.index('\nif __name__ == "__main__":')
src_functions_only = src_patched[:main_guard_idx]

exec(compile(src_functions_only, src_path, "exec"), mod.__dict__)

# ── Now run exactly the same flow as the original __main__ ──────────────────
import pandas as pd
import numpy as np
import glob as _glob

print("=" * 60)
print("  BACKTEST: MAX_TRADES_PER_DAY = 1  (BE 2.0×SL + Trail)")
print("  2025 | $10,000 | 1% risk | FTMO costs")
print("=" * 60)

# ── 1. Adatbetöltés ──────────────────────────────────────────────────────────
csv_candidates = _glob.glob(os.path.join(os.path.dirname(__file__), "*XAUUSD*15*.csv")) + \
                 _glob.glob(os.path.join(os.path.dirname(__file__), "*xauusd*.csv"))
df = None
for cp in dict.fromkeys(csv_candidates):
    if os.path.exists(cp):
        try:
            df = mod.load_mt5_csv(cp)
            print(f"  CSV: {os.path.basename(cp)} → {len(df):,} gyertya")
            break
        except Exception:
            pass

if df is None:
    print("  Szintetikus adat generálása...")
    df = mod.generate_synthetic_xauusd(years=4, freq="15min")

print(f"  Időszak: {df.index[0].date()} → {df.index[-1].date()}")

# ── 2. HTF bias (teljes adatsor) ─────────────────────────────────────────────
htf_full = mod.compute_htf_bias(df)

# ── 3. Warmup + 2025 filter ──────────────────────────────────────────────────
YEAR = mod.BACKTEST_YEAR  # 2025
warmup_start = pd.Timestamp(f"{YEAR}-01-01") - pd.Timedelta(hours=8)
df_w   = df[df.index >= warmup_start].copy()
bias_w = htf_full.reindex(df_w.index, method="ffill").fillna(0)

print(f"  Szignálok generálása (warmup: {warmup_start.date()})...")
signals_w = mod.generate_signals(df_w, htf_bias=bias_w)

year_mask   = df_w.index.year == YEAR
df_bt       = df_w[year_mask].copy()
signals_bt  = {k: v[year_mask] for k, v in signals_w.items()}

n_raw = int(signals_bt["long_entries"].sum())
print(f"  Nyers long szignálok (2025): {n_raw}")
print(f"  MAX_TRADES_PER_DAY = {mod.MAX_TRADES_PER_DAY}  ← 1-trade limit aktív")

# ── 4. Simulate: BE 2.0×SL + Trail ──────────────────────────────────────────
print("\n  Szimuláció: BE 2.0×SL + Trail...")
result = mod.simulate_breakeven(df_bt, signals_bt, be_rr=2.0, trail_after_be=True)

if result is None or len(result) == 0:
    print("  [!] Nincs trade – ellenőrizd az adatforrást.")
    sys.exit(1)

closed = result[result["Exit Type"] != "OPEN"].copy()
n      = len(closed)
if n == 0:
    print("  [!] Nincs lezárt trade.")
    sys.exit(1)

# ── 5. Statisztikák ───────────────────────────────────────────────────────────
wr        = (closed["PnL ($)"] > 0).mean() * 100
total_pnl = closed["PnL ($)"].sum()
fees      = closed["Fees ($)"].sum() if "Fees ($)" in closed.columns else 0.0
final_eq  = mod.INIT_CASH + total_pnl
max_dd    = closed["PnL ($)"].cumsum().cummin().min()
max_dd_pct = abs(max_dd) / mod.INIT_CASH * 100

tp_ex  = closed[closed["Exit Type"] == "TP"]
be_ex  = closed[closed["Exit Type"] == "SL-BE"]
sl_ex  = closed[closed["Exit Type"] == "SL"]
be_tri = closed[closed["BE Triggered"] == True] if "BE Triggered" in closed.columns else pd.DataFrame()

closed["Month"] = pd.to_datetime(closed["Entry Time"]).dt.to_period("M")
monthly = closed.groupby("Month").agg(
    Kötés    = ("PnL ($)", "count"),
    Win_Rate = ("PnL ($)", lambda x: f"{(x > 0).mean()*100:.0f}%"),
    PnL_USD  = ("PnL ($)", lambda x: f"${x.sum():+.2f}"),
    PnL_Pct  = ("PnL ($)", lambda x: f"{x.sum()/mod.INIT_CASH*100:+.2f}%"),
)

n_months  = closed["Month"].nunique()
avg_m_pct = total_pnl / n_months / mod.INIT_CASH * 100 if n_months > 0 else 0

print(f"""
══════════════════════════════════════════════════════════
  EREDMÉNY: BE 2.0×SL + Trail  |  MAX 1 TRADE/NAP
══════════════════════════════════════════════════════════
  Lezárt kötések      : {n}
  Win Rate            : {wr:.1f}%
  Összes PnL (nettó)  : ${total_pnl:+,.2f}
  FTMO jutalék össz.  : ${fees:,.2f}
  Végső tőke          : ${final_eq:,.2f}
  Hozam               : {(final_eq/mod.INIT_CASH - 1)*100:+.2f}%
  Max Drawdown (est.) : {max_dd_pct:.2f}%  {'✓ FTMO OK' if max_dd_pct <= 10 else '✗ LIMIT!'}
  Átlag havi hozam    : {avg_m_pct:+.2f}%
  Havi 5% cél         : {'✓ Elért' if avg_m_pct >= 5.0 else f'✗ Hiány: {5.0 - avg_m_pct:.2f}%'}

  ── Exit típusok ────────────────────────────────────────
  TP   (profit célár) : {len(tp_ex):>3} db  ({len(tp_ex)/n*100:.1f}%)   ${tp_ex['PnL ($)'].sum():+,.2f}
  SL-BE (BE védte)    : {len(be_ex):>3} db  ({len(be_ex)/n*100:.1f}%)   ${be_ex['PnL ($)'].sum():+,.2f}
  SL   (hagyományos)  : {len(sl_ex):>3} db  ({len(sl_ex)/n*100:.1f}%)   ${sl_ex['PnL ($)'].sum():+,.2f}
  BE trigger aktív    : {len(be_tri):>3} trade ({len(be_tri)/n*100:.1f}%)
""")

print("  Havi bontás:")
print("  " + "─" * 56)
print(monthly.to_string())
print()

# ── 6. Összehasonlítás: 2-trade vs 1-trade ───────────────────────────────────
print("""
══════════════════════════════════════════════════════════
  ÖSSZEHASONLÍTÁS: 2 trade/nap vs 1 trade/nap
══════════════════════════════════════════════════════════""")
print(f"  {'Mutató':<28} {'2 trade/nap':>12}  {'1 trade/nap':>12}")
print(f"  {'─'*55}")
print(f"  {'Kötések száma':<28} {'41':>12}  {n:>12}")
print(f"  {'Win Rate':<28} {'61.0%':>12}  {wr:.1f}%".rjust(56))
print(f"  {'Összes hozam':<28} {'+45.34%':>12}  {(final_eq/mod.INIT_CASH-1)*100:>+11.2f}%")
print(f"  {'Átlag havi hozam':<28} {'+4.12%':>12}  {avg_m_pct:>+11.2f}%")
print(f"  {'Max Drawdown':<28} {'2.72%':>12}  {max_dd_pct:>11.2f}%")
print(f"  {'─'*55}")
print(f"  Kötés/hónap (2-trade): {41/n_months:.1f}  →  (1-trade): {n/n_months:.1f}")
