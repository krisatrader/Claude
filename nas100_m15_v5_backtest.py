"""
NAS100 ORB Regime Engine v5 — M15 60-day NQ=F
Két módosítás v4-hez képest:

  [B] Module B ADX cap: max ADX_sm=32 — megakadályozza a belépést magas-ADX,
      irányváltó piacokon (pl. jún.10: ADX_sm=42.7, |PDI-NDI_sm|=3.2 → tévesen range)
      Megtartott logika: OR-alapú rezsim-detektálás (range = bármelyik feltétel)
      Hozzáadott szűrő: Module B csak ha ADX_sm < MAX_MODB_ADX_SM

  [C] Adaptív bar-strength: 0.60 → 0.50 ha ADX_sm > ADX_STRONG_THRESH (35)
      Erősen trendelő napokon (ADX_sm>35) az első breakout bárat is elfogadja,
      ne kelljen egy barral később és rosszabb áron belépni.

Cél: v4 ápr/máj megtartása + júniusi rés csökkentése (v3: +$24k → v5: közelítés)
"""
import yfinance as yf
import pandas as pd
import numpy as np
from datetime import datetime
import warnings
warnings.filterwarnings('ignore')

# ─── CONFIG ──────────────────────────────────────────────────────────────────
INITIAL_BALANCE    = 100_000
POINT_VALUE        = 20

# Regime detection (unchanged from v4)
SMOOTH_SPAN        = 16
TREND_ADX_MIN      = 25.0
TREND_CI_MAX       = 52.0
TREND_PDI_NDI_MIN  = 10.0
RANGE_ADX_MAX      = 22.0
RANGE_CI_MIN       = 57.0
RANGE_PDI_NDI_MAX  = 6.0

# Module A — ORB Trend (v5 change: adaptive bar strength)
RISK_BASE_A        = 0.010
RISK_GRADE_A       = 0.015
ADX_GRADE_A        = 28.0
CI_GRADE_A         = 45.0
ATR_SL_A           = 1.5
PARTIAL_R          = 1.5
TRAIL_MULT         = 2.0
BAR_STRENGTH       = 0.60       # [C] normál bar-close strength
BAR_STRENGTH_STRONG = 0.50      # [C] v5 NEW: ha ADX_sm > ADX_STRONG_THRESH
ADX_STRONG_THRESH  = 35.0       # [C] v5 NEW: erős trend határ
ORB_QUALITY        = 0.35

# Module B — BB Reversion (v5 change: ADX cap)
RISK_B             = 0.005
ATR_SL_B           = 1.2
RSI_LONG           = 25
RSI_SHORT          = 75
MAX_B_PER_DAY      = 2
BB_PERIOD          = 20
BB_STD             = 2.0
MAX_MODB_ADX_SM    = 32.0       # [B] v5 NEW: Module B max ADX_sm plafon

SPREAD_PTS         = 0.5
MAX_DAILY_DD       = 0.05

# ─── DATA + INDICATORS ───────────────────────────────────────────────────────
print("Fetching NQ=F M15 (60 days)...")
raw = yf.download('NQ=F', period='60d', interval='15m', progress=False)
if isinstance(raw.columns, pd.MultiIndex): raw.columns = raw.columns.get_level_values(0)
raw.index = pd.to_datetime(raw.index, utc=True)

def atr(df, n):
    h, l, c = df['High'], df['Low'], df['Close']
    return pd.concat([h-l, (h-c.shift(1)).abs(), (l-c.shift(1)).abs()],
                     axis=1).max(axis=1).ewm(span=n, adjust=False).mean()

def adx_full(df, n):
    h, l, c = df['High'], df['Low'], df['Close']
    up = h - h.shift(1); dn = l.shift(1) - l
    pdm = pd.Series(np.where((up>dn)&(up>0), up, 0.), index=df.index)
    ndm = pd.Series(np.where((dn>up)&(dn>0), dn, 0.), index=df.index)
    a = atr(df, n)
    pdi = 100 * pdm.ewm(span=n, adjust=False).mean() / a
    ndi = 100 * ndm.ewm(span=n, adjust=False).mean() / a
    dx = (100 * (pdi-ndi).abs() / (pdi+ndi).replace(0, np.nan)).fillna(0)
    return dx.ewm(span=n, adjust=False).mean(), pdi, ndi

def chop(df, n):
    h, l, c = df['High'], df['Low'], df['Close']
    tr = pd.concat([h-l, (h-c.shift(1)).abs(), (l-c.shift(1)).abs()], axis=1).max(axis=1)
    return (100 * np.log10(tr.rolling(n).sum() /
            (h.rolling(n).max() - l.rolling(n).min()).replace(0, np.nan)) / np.log10(n))

def rsi_c(s, n):
    d = s.diff()
    g = d.where(d>0, 0).ewm(span=n, adjust=False).mean()
    lo = (-d).where(d<0, 0).ewm(span=n, adjust=False).mean()
    return 100 - 100 / (1 + g / lo.replace(0, np.nan))

print("Computing indicators...")
raw['ATR'] = atr(raw, 14)
raw['ADX'], raw['PDI'], raw['NDI'] = adx_full(raw, 14)
raw['CI']     = chop(raw, 14)
raw['EMA200'] = raw['Close'].ewm(span=200, adjust=False).mean()
raw['DonHi']  = raw['High'].rolling(10).max().shift(1)
raw['DonLo']  = raw['Low'].rolling(10).min().shift(1)
raw['RSI7']   = rsi_c(raw['Close'], 7)
raw['BB_mid'] = raw['Close'].rolling(BB_PERIOD).mean()
raw['BB_std'] = raw['Close'].rolling(BB_PERIOD).std()
raw['BB_up']  = raw['BB_mid'] + BB_STD * raw['BB_std']
raw['BB_lo']  = raw['BB_mid'] - BB_STD * raw['BB_std']
raw['ADX_sm'] = raw['ADX'].ewm(span=SMOOTH_SPAN, adjust=False).mean()
raw['CI_sm']  = raw['CI'].ewm(span=SMOOTH_SPAN, adjust=False).mean()
raw['PDI_NDI_sm'] = (raw['PDI'] - raw['NDI']).ewm(span=SMOOTH_SPAN, adjust=False).mean()
raw = raw.dropna()
print(f"  {len(raw)} bars | {raw.index[0].date()} → {raw.index[-1].date()}")

def get_regime(as_, cs_, ps_):
    # OR-based range (unchanged from v4)
    if as_ >= TREND_ADX_MIN and cs_ <= TREND_CI_MAX and abs(ps_) >= TREND_PDI_NDI_MIN:
        return 'trend'
    if as_ < RANGE_ADX_MAX or cs_ > RANGE_CI_MIN or abs(ps_) < RANGE_PDI_NDI_MAX:
        return 'range'
    return 'neutral'

# ─── ENGINE ──────────────────────────────────────────────────────────────────
balance = INITIAL_BALANCE; log = []; peak_bal = INITIAL_BALANCE
cur_day = None; daily_start = INITIAL_BALANCE; daily_blocked = False
orb_hi = orb_lo = None; orb_done = ny_traded = False; range_td = 0; trade = None

def close_t(t, ep, ts, reason, partial=False, pvol=None):
    global balance, log
    vol = pvol if partial else t['vol']
    if vol <= 0: return
    mult = 1 if t['dir'] == 'long' else -1
    pnl = (ep - t['entry']) * mult * vol * POINT_VALUE
    balance += pnl
    log.append({
        'entry_time': t['entry_time'], 'exit_time': ts,
        'module': t['module'], 'dir': t['dir'],
        'entry': t['entry'], 'exit': ep, 'vol': vol, 'pnl': pnl,
        'reason': reason, 'partial': partial, 'regime': t.get('regime', ''),
        'adx_sm': t.get('adx_sm', 0), 'ci_sm': t.get('ci_sm', 0),
        'pdi_ndi_sm': t.get('pdi_ndi_sm', 0),
        'bar_str': t.get('bar_str', 0),       # v5: bar strength naplózása
        'modb_blocked': t.get('modb_blocked', False)   # v5: ADX cap hatás jelölése
    })
    if partial:
        t['vol'] -= vol
    else:
        t['vol'] = 0

print("Running v5 backtest...")
b_blocked_adx = 0   # [B] számlálás: hányszor blokkolt az ADX cap
a_relaxed_bstr = 0  # [C] számlálás: hányszor használta a relaxált bar strength-et

for ts, row in raw.iterrows():
    p   = float(row['Close']); hi = float(row['High']); lo = float(row['Low'])
    av  = float(row['ATR']);   dv = float(row['ADX']);  cv = float(row['CI'])
    pv  = float(row['PDI']);   nv = float(row['NDI']);  ev = float(row['EMA200'])
    dh  = float(row['DonHi']); dl = float(row['DonLo'])
    rv  = float(row['RSI7']);  bu = float(row['BB_up']); bl = float(row['BB_lo'])
    bm  = float(row['BB_mid'])
    as_ = float(row['ADX_sm']); cs_ = float(row['CI_sm']); ps_ = float(row['PDI_NDI_sm'])
    hm  = (ts.hour, ts.minute)
    regime = get_regime(as_, cs_, ps_)

    bd = ts.date()
    if bd != cur_day:
        cur_day = bd; daily_start = balance; daily_blocked = False
        orb_hi = orb_lo = None; orb_done = ny_traded = False; range_td = 0

    if balance < daily_start * (1 - MAX_DAILY_DD):
        daily_blocked = True
        if trade:
            close_t(trade, p, ts, 'daily_dd'); trade = None
        continue

    # ORB range építés
    if (14, 30) <= hm < (15, 0):
        orb_hi = max(orb_hi, hi) if orb_hi else hi
        orb_lo = min(orb_lo, lo) if orb_lo else lo
    if hm == (15, 0) and orb_hi and not orb_done:
        orb_done = True

    # ── Nyitott pozíció kezelése ──────────────────────────────────────────────
    if trade and trade['vol'] > 0:
        t = trade; m = 1 if t['dir'] == 'long' else -1
        pts = (p - t['entry']) * m; pr = t['pts_risk']

        if t['module'] == 'A':
            # Partial TP at 1.5R
            if not t['pd'] and pts >= pr * PARTIAL_R:
                half = max(1, t['vf'] // 2)
                if half <= t['vol']:
                    close_t(t, p, ts, 'partial_tp', True, half)
                    t['sl'] = t['entry'] + m; t['be'] = True; t['pd'] = True
            # Break-even at 1R
            if not t['be'] and pts >= pr:
                t['sl'] = t['entry'] + m; t['be'] = True
            # ATR trailing after break-even
            if t['be'] and t['vol'] > 0:
                if t['dir'] == 'long':
                    tr = p - av * TRAIL_MULT
                    t['ts2'] = max(t['sl'], tr) if t['ts2'] else tr
                    t['sl'] = max(t['sl'], t['ts2'])
                else:
                    tr = p + av * TRAIL_MULT
                    t['ts2'] = min(t['sl'], tr) if t['ts2'] else tr
                    t['sl'] = min(t['sl'], t['ts2'])
            # Exit checks
            slh = (t['dir'] == 'long' and lo <= t['sl']) or (t['dir'] == 'short' and hi >= t['sl'])
            dnh = t['be'] and ((t['dir'] == 'long' and lo <= dl) or (t['dir'] == 'short' and hi >= dh))
            eod = hm >= (21, 0)
            if slh:
                close_t(t, t['sl'], ts, 'sl'); trade = None
            elif dnh:
                close_t(t, dl if t['dir'] == 'long' else dh, ts, 'donchian'); trade = None
            elif eod and t['vol'] > 0:
                close_t(t, p, ts, 'session_close'); trade = None

        elif t['module'] == 'B':
            tp_h = (t['dir'] == 'long' and hi >= bm) or (t['dir'] == 'short' and lo <= bm)
            slh  = (t['dir'] == 'long' and lo <= t['sl']) or (t['dir'] == 'short' and hi >= t['sl'])
            eod  = hm >= (21, 0)
            if slh:
                close_t(t, t['sl'], ts, 'sl'); trade = None
            elif tp_h:
                close_t(t, bm, ts, 'bb_tp'); trade = None
            elif eod and t['vol'] > 0:
                close_t(t, p, ts, 'session_close'); trade = None
        continue

    if daily_blocked: continue

    # ── MODULE A — ORB Trend entry ────────────────────────────────────────────
    if regime == 'trend' and orb_done and not ny_traded and (15, 0) <= hm < (21, 0):
        orb_r = (orb_hi - orb_lo) if (orb_hi and orb_lo) else 0
        if orb_r >= av * ORB_QUALITY:
            bsp = (p - lo) / max(hi - lo, 0.01)

            # [C] v5: adaptív bar strength — erős trend napokon lazítás 0.60 → 0.50
            bstr_thr = BAR_STRENGTH_STRONG if as_ > ADX_STRONG_THRESH else BAR_STRENGTH
            used_relaxed = (as_ > ADX_STRONG_THRESH)

            if p > orb_hi + SPREAD_PTS and p > ev and pv > nv and bsp >= bstr_thr:
                rk = RISK_GRADE_A if dv > ADX_GRADE_A and cv < CI_GRADE_A else RISK_BASE_A
                sd = av * ATR_SL_A; rpc = sd * POINT_VALUE
                v = max(1, int(balance * rk / rpc)) if rpc > 0 else 1
                if used_relaxed: a_relaxed_bstr += 1
                trade = {
                    'module': 'A', 'dir': 'long', 'entry': p, 'sl': p - sd,
                    'vol': v, 'vf': v, 'entry_time': ts, 'be': False, 'pd': False,
                    'ts2': None, 'pts_risk': sd, 'regime': regime,
                    'adx_sm': as_, 'ci_sm': cs_, 'pdi_ndi_sm': ps_,
                    'bar_str': bsp, 'modb_blocked': False
                }
                ny_traded = True

            elif p < orb_lo - SPREAD_PTS and p < ev and nv > pv and bsp <= (1.0 - bstr_thr):
                rk = RISK_GRADE_A if dv > ADX_GRADE_A and cv < CI_GRADE_A else RISK_BASE_A
                sd = av * ATR_SL_A; rpc = sd * POINT_VALUE
                v = max(1, int(balance * rk / rpc)) if rpc > 0 else 1
                if used_relaxed: a_relaxed_bstr += 1
                trade = {
                    'module': 'A', 'dir': 'short', 'entry': p, 'sl': p + sd,
                    'vol': v, 'vf': v, 'entry_time': ts, 'be': False, 'pd': False,
                    'ts2': None, 'pts_risk': sd, 'regime': regime,
                    'adx_sm': as_, 'ci_sm': cs_, 'pdi_ndi_sm': ps_,
                    'bar_str': bsp, 'modb_blocked': False
                }
                ny_traded = True

    # ── MODULE B — BB Reversion entry ─────────────────────────────────────────
    # [B] v5: ADX cap — blokkolja a belépést ha ADX_sm > MAX_MODB_ADX_SM
    elif regime == 'range' and range_td < MAX_B_PER_DAY and (9, 0) <= hm < (21, 0):
        if as_ >= MAX_MODB_ADX_SM:
            # [B] ADX cap aktív — naplózás diagnosztikához
            b_blocked_adx += 1
        else:
            sd = av * ATR_SL_B; rpc = sd * POINT_VALUE
            v = max(1, int(balance * RISK_B / rpc)) if rpc > 0 else 1

            if p < bl and rv < RSI_LONG and (bm - p) > sd and p > ev:
                trade = {
                    'module': 'B', 'dir': 'long', 'entry': p, 'sl': p - sd,
                    'vol': v, 'vf': v, 'entry_time': ts, 'be': False, 'pd': False,
                    'ts2': None, 'pts_risk': sd, 'regime': regime,
                    'adx_sm': as_, 'ci_sm': cs_, 'pdi_ndi_sm': ps_,
                    'bar_str': 0, 'modb_blocked': False
                }
                range_td += 1
            elif p > bu and rv > RSI_SHORT and (p - bm) > sd and p < ev:
                trade = {
                    'module': 'B', 'dir': 'short', 'entry': p, 'sl': p + sd,
                    'vol': v, 'vf': v, 'entry_time': ts, 'be': False, 'pd': False,
                    'ts2': None, 'pts_risk': sd, 'regime': regime,
                    'adx_sm': as_, 'ci_sm': cs_, 'pdi_ndi_sm': ps_,
                    'bar_str': 0, 'modb_blocked': False
                }
                range_td += 1

# ─── RESULTS ─────────────────────────────────────────────────────────────────
df_log = pd.DataFrame(log)
print()
print("=" * 70)
print("NAS100 ORB REGIME ENGINE v5 — M15 BACKTEST")
print("[B] Module B ADX cap = %.0f  |  [C] Adaptive bar strength >%.0f → %.2f" % (
      MAX_MODB_ADX_SM, ADX_STRONG_THRESH, BAR_STRENGTH_STRONG))
print("=" * 70)

wins   = df_log[df_log['pnl'] > 0]
losses = df_log[df_log['pnl'] <= 0]
wr     = len(wins) / len(df_log) * 100
net    = df_log['pnl'].sum()
avg_w  = wins['pnl'].mean() if len(wins) else 0
avg_l  = losses['pnl'].mean() if len(losses) else 0
rr     = abs(avg_w / avg_l) if avg_l != 0 else 0

run = INITIAL_BALANCE; pk = INITIAL_BALANCE; mdd = 0; ftmo = 0
for p2 in df_log.sort_values('exit_time')['pnl']:
    run += p2; pk = max(pk, run)
    mdd = max(mdd, (pk - run) / pk)
    ftmo = max(ftmo, (INITIAL_BALANCE - run) / INITIAL_BALANCE)

df_log['month'] = df_log['exit_time'].dt.to_period('M')
monthly   = df_log.groupby('month')['pnl'].sum()
mod_stats = df_log.groupby('module')['pnl'].agg(['count', 'sum', 'mean'])
exit_stats = df_log['reason'].value_counts()

print(f"Period:              {df_log['entry_time'].min().date()} → {df_log['exit_time'].max().date()}")
print(f"Trade events:        {len(df_log)}")
print(f"Win rate:            {wr:.1f}%")
print(f"Avg win:            ${avg_w:,.0f}  |  Avg loss: ${avg_l:,.0f}")
print(f"Reward/Risk:         {rr:.2f}")
print(f"Net P&L:            ${net:,.0f}  ({net/INITIAL_BALANCE*100:.2f}%)")
print(f"Final balance:      ${balance:,.0f}")
print(f"Peak-to-trough DD:   {mdd*100:.2f}%")
print(f"FTMO DD (vs $100k):  {ftmo*100:.2f}%")
print()

# v5 specifikus diagnosztika
print(f"v5 módosítások hatása:")
print(f"  [B] ADX cap (>{MAX_MODB_ADX_SM:.0f}) blokkolta Module B-t: {b_blocked_adx}× alkalommal")
print(f"  [C] Relaxált bar strength (<{ADX_STRONG_THRESH:.0f} ADX_sm): {a_relaxed_bstr}× Module A belépés")
print()

print("Havi bontás:")
run2 = INITIAL_BALANCE
for m, mp in monthly.items():
    run2 += mp
    flag = "← VESZTESÉG" if mp < 0 else "✓"
    print(f"  {m}:  ${mp:>9,.0f}  {flag}")
print()

print("Modulonként:")
print(mod_stats.rename(columns={'count': 'events', 'sum': 'total', 'mean': 'avg'}).to_string())
print()
print("Kilépési okok:")
print(exit_stats.to_string())
print()

# Összehasonlítás v3/v4-gyel
print("─" * 70)
print("ÖSSZEHASONLÍTÁS:")
print(f"  v3: Net +$20,956 | Apr +$2,654  | Máj -$5,940  | Jún +$24,242 | DD 14.1%")
print(f"  v4: Net +$18,246 | Apr +$5,517  | Máj +$2,906  | Jún  +$9,823 | DD  5.8%")
print(f"  v5: Net ${net:+,.0f} | ", end="")
for m, mp in monthly.items():
    print(f"{str(m)[-2:]} ${mp:+,.0f}  | ", end="")
print(f"DD {mdd*100:.1f}%")
print("─" * 70)
print()

print("Trade log:")
disp = df_log[['entry_time', 'exit_time', 'module', 'dir', 'entry', 'exit',
               'pnl', 'reason', 'regime', 'adx_sm', 'ci_sm', 'pdi_ndi_sm', 'bar_str']].copy()
disp['entry_time']  = disp['entry_time'].dt.strftime('%m-%d %H:%M')
disp['exit_time']   = disp['exit_time'].dt.strftime('%m-%d %H:%M')
disp['pnl']         = disp['pnl'].map('${:,.0f}'.format)
disp['entry']       = disp['entry'].map('{:.1f}'.format)
disp['exit']        = disp['exit'].map('{:.1f}'.format)
disp['adx_sm']      = disp['adx_sm'].map('{:.1f}'.format)
disp['ci_sm']       = disp['ci_sm'].map('{:.1f}'.format)
disp['pdi_ndi_sm']  = disp['pdi_ndi_sm'].map('{:.1f}'.format)
disp['bar_str']     = disp['bar_str'].map('{:.2f}'.format)
print(disp.to_string(index=False))
