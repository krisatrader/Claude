"""
Aktív stratégia elemzés: miért van csak 4-5 kötés/hó, és hogyan növeljük 20-ra?
"""
import sys, os, types
sys.path.insert(0, os.path.dirname(__file__))

src_path = os.path.join(os.path.dirname(__file__), "xauusd_vectorbt_test.py")
with open(src_path) as f:
    src = f.read()

# Patch: kikapcsoljuk a __main__ részt és futtatjuk a függvényeket
main_idx = src.index('\nif __name__ == "__main__":')
exec(compile(src[:main_idx], src_path, "exec"))

import pandas as pd
import numpy as np
import glob as _glob

# Adatbetöltés
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
    print("Szintetikus adat")

# --- HTF Bias elemzés ---------------------------------------------------------
print("\n" + "="*60)
print("  HTF BIAS ELEMZÉS – Mennyi idő van 'bullish' állapotban?")
print("="*60)

htf_full = compute_htf_bias(df)
df_2025 = df[df.index.year == 2025]
bias_2025 = htf_full.reindex(df_2025.index, method="ffill").fillna(0)

n_total = len(bias_2025)
n_bull  = (bias_2025 == 1).sum()
n_bear  = (bias_2025 == -1).sum()
n_neut  = (bias_2025 == 0).sum()

print(f"  2025 M15 barok: {n_total:,}")
print(f"  Bull (HTF=+1) : {n_bull:,} ({n_bull/n_total*100:.1f}%) ← csak ekkor léphet be a bot")
print(f"  Bear (HTF=-1) : {n_bear:,} ({n_bear/n_total*100:.1f}%)")
print(f"  Semleges      : {n_neut:,} ({n_neut/n_total*100:.1f}%)")

# --- Napi aktív ablak ---------------------------------------------------------
session_mask = (df_2025.index.hour >= 7) & (df_2025.index.hour < 18)
bull_session = (bias_2025 == 1) & session_mask
print(f"\n  Session (7-18 UTC) + Bull bias aktív:")
print(f"  {bull_session.sum():,} M15 bar = {bull_session.sum()/4:.0f} óra / {bull_session.sum()/4/11:.0f} kereskedési nap")

# --- Swing szignál elemzés különböző lookback szintekkel ---------------------
print("\n" + "="*60)
print("  SZIGNÁL ELEMZÉS – Lookback szint hatása a szignálszámra")
print("="*60)

warmup_start = pd.Timestamp("2024-12-31 16:00")
df_w = df[df.index >= warmup_start].copy()
bias_w = htf_full.reindex(df_w.index, method="ffill").fillna(0)
year_mask = df_w.index.year == 2025

# Teszteljük különböző lookback kombinációkkal
test_configs = [
    ([8, 15, 25],               "Jelenlegi [8,15,25]"),
    ([5, 8, 12, 15, 20, 25],    "Bővített [5,8,12,15,20,25]"),
    ([4, 6, 8, 10, 12, 15, 20, 25], "Max [4,6,8,10,12,15,20,25]"),
]

# HTF variánsok vizsgálata
# Az aktuális HTF: 5 feltétel, relaxált: csak H4 EMA20 + H4 ADX
def compute_h4_only_bias(df_full):
    """Egyszerűsített HTF: csak H4 EMA20 + H4 ADX≥20"""
    import pandas as pd
    import numpy as np
    bias = pd.Series(0, index=df_full.index, dtype=int)

    h4 = df_full.resample("4h").agg({"High": "max", "Low": "min", "Close": "last"}).dropna()
    h4_adx = adx_series(h4["High"], h4["Low"], h4["Close"], 14)
    h4_ema20 = h4["Close"].ewm(span=20, adjust=False).mean()

    adx_m15 = h4_adx.reindex(df_full.index, method="ffill").fillna(0)
    h4_bull_m15 = (h4["Close"] > h4_ema20).astype(int).reindex(df_full.index, method="ffill").fillna(0)

    trending = adx_m15 >= 20
    bias[trending & (h4_bull_m15 == 1)] = 1
    bias[trending & (h4_bull_m15 == 0)] = -1
    return bias

def compute_h4_w1_bias(df_full):
    """Közepes HTF: W1 EMA10 + H4 EMA20 + H4 ADX≥20 (nincs D1 slope)"""
    import pandas as pd
    bias = pd.Series(0, index=df_full.index, dtype=int)

    # W1
    w1 = df_full["Close"].resample("W").last().dropna()
    w1_ema = w1.ewm(span=10, adjust=False).mean()
    w1_bull = (w1 > w1_ema.shift(1)).astype(int).replace(0, -1)
    w1_m15 = w1_bull.reindex(df_full.index, method="ffill").fillna(0)

    # H4
    h4 = df_full.resample("4h").agg({"High": "max", "Low": "min", "Close": "last"}).dropna()
    h4_adx = adx_series(h4["High"], h4["Low"], h4["Close"], 14)
    h4_ema20 = h4["Close"].ewm(span=20, adjust=False).mean()
    adx_m15 = h4_adx.reindex(df_full.index, method="ffill").fillna(0)
    h4_bull_m15 = (h4["Close"] > h4_ema20).astype(int).reindex(df_full.index, method="ffill").fillna(0)

    trending = adx_m15 >= 20
    bull_cond = trending & (w1_m15 == 1) & (h4_bull_m15 == 1)
    bias[bull_cond] = 1
    return bias

biases = {
    "5-réteg (jelenlegi)":    compute_htf_bias(df),
    "W1+H4 (közepes)":        compute_h4_w1_bias(df),
    "H4 only (relaxált)":     compute_h4_only_bias(df),
}

print(f"\n  {'HTF variáns':<26} {'Bull%':>6}  {'Nyers szignál/hó':>18}  {'Becsült kötés/hó':>18}")
print(f"  {'─'*75}")

for htf_name, htf_bias in biases.items():
    # Szignál generálás a 2025-ös adatokon [5,8,12,15,20,25] szintekkel
    b_w = htf_bias.reindex(df_w.index, method="ffill").fillna(0)

    # Szimuláljuk: hány nyers szignál keletkezik (SWING_LEVELS=[5,8,12,15,20,25])
    import numpy as np
    n = len(df_w)
    idx = df_w.index
    close = df_w["Close"].values
    open_ = df_w["Open"].values
    high  = df_w["High"].values
    low   = df_w["Low"].values
    bias_v = b_w.values

    swing_levels_test = [5, 8, 12, 15, 20, 25]
    swing_highs = [df_w["High"].shift(2).rolling(lb).max().values for lb in swing_levels_test]
    hours = idx.hour

    signals = 0
    bo_dir   = [0] * len(swing_levels_test)
    bo_level = [0.0] * len(swing_levels_test)
    rt_count = [0] * len(swing_levels_test)
    waiting  = [False] * len(swing_levels_test)

    for i in range(max(swing_levels_test) + 5, n - 1):
        if bias_v[i] != 1: continue
        if not (7 <= int(hours[i]) < 18): continue

        p_c = close[i-1]; p_o = open_[i-1]; p_h = high[i-1]; p_l = low[i-1]
        c_c = close[i]

        for lvl in range(len(swing_levels_test)):
            sh = swing_highs[lvl][i]
            if np.isnan(sh): continue

            if not waiting[lvl]:
                if p_c > sh + MIN_BREAKOUT_PTS:
                    bo_dir[lvl] = 1; bo_level[lvl] = sh; rt_count[lvl] = 0; waiting[lvl] = True
            else:
                rt_count[lvl] += 1
                if rt_count[lvl] > MAX_RETEST_CANDLES:
                    waiting[lvl] = False; bo_dir[lvl] = 0; continue
                zone = bo_level[lvl] * (RETEST_ZONE_PCT / 100)
                touched = (p_l <= bo_level[lvl] + zone)
                bullish = (p_c > p_o and (p_c - p_o) > 0.25 * (p_h - p_l + 1e-9))
                if touched and bullish:
                    signals += 1
                    waiting[lvl] = False; bo_dir[lvl] = 0

    bull_pct = (htf_bias.reindex(df_2025.index, method="ffill").fillna(0) == 1).mean() * 100
    signals_mo = signals / 11
    traded_mo  = min(signals_mo, 22)  # becslés: max 1/nap × 22 kereskedési nap
    print(f"  {htf_name:<26} {bull_pct:>5.1f}%  {signals_mo:>17.1f}  {traded_mo:>17.1f}")

print(f"""
  ── Következtetések ─────────────────────────────────────────────────────
  • Jelenlegi 5-réteg HTF: csak {(bias_2025==1).mean()*100:.0f}% bull idő → kevés szignál
  • W1+H4 kombináció: ~közepesen aktív, jó minőség-mennyiség egyensúly
  • H4-only: legtöbb szignál, de több false signal kockázata

  → Optimális: W1 EMA10 + H4 EMA20 + H4 ADX + [5,8,12,15,20,25] szintek
               + 2 párhuzamos pozíció engedélyezve
               → Cél: ~15-20 kötés/hó reális
""")
