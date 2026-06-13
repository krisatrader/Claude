"""
NAS100 ORB Regime Engine v3 — M15 Backtest (60 nap, NQ=F)
Stratégia v3 logika:
  - NY ORB: 14:30-15:00 UTC (2 M15 bar)
  - Opcionális London ORB: 8:00-8:30 UTC
  - Regime: ADX>22, CI<55
  - Irány: EMA200 + PDI>NDI / NDI>PDI
  - ORB minőség: range >= ATR*0.35
  - Partial TP: 50% at 1.5R → BE → ATR*2.0 trailing
  - Grade-A: 1.5% risk ha ADX>28 és CI<45 (else 1%)
  - Session close: 21:00 UTC
"""
import yfinance as yf
import pandas as pd
import numpy as np
from datetime import datetime, timedelta
import warnings
warnings.filterwarnings('ignore')

# ─── CONFIG ──────────────────────────────────────────────────────────────────
INITIAL_BALANCE  = 100_000
RISK_BASE        = 0.010
RISK_GRADE_A     = 0.015
ADX_GRADE_A      = 28
CI_GRADE_A       = 45
ADX_MIN          = 22
CI_MAX           = 55
ATR_MULT_SL      = 1.5
PARTIAL_R        = 1.5
TRAIL_MULT       = 2.0
EMA_PERIOD       = 200
ATR_N = ADX_N = CI_N = 14
DONCHIAN_N       = 10
SPREAD_PTS       = 0.5
POINT_VALUE      = 20        # NQ=F: $20/pont/contract
MAX_DAILY_DD     = 0.05
ORB_QUALITY      = 0.35
PDI_NDI_MIN_DIFF = 0         # NY: nincs extra PDI/NDI limit (EMA200+PDI>NDI elég)
LONDON_PDI_NDI   = 15

ENABLE_LONDON    = False     # London session tesztelése: True/False

SESSIONS = {
    'ny':     {'orb_start':(14,30), 'orb_end':(15, 0), 'trade_until':(21, 0)},
}
if ENABLE_LONDON:
    SESSIONS['london'] = {'orb_start':(8,0), 'orb_end':(8,30), 'trade_until':(14,0)}

# ─── DATA ────────────────────────────────────────────────────────────────────
print("Fetching NQ=F M15 data (60 days)...")
raw = yf.download('NQ=F', period='60d', interval='15m', progress=False)
if isinstance(raw.columns, pd.MultiIndex):
    raw.columns = raw.columns.get_level_values(0)
raw.index = pd.to_datetime(raw.index, utc=True)
print(f"  {len(raw)} bars | {raw.index[0].date()} → {raw.index[-1].date()}")

# ─── INDICATORS ──────────────────────────────────────────────────────────────
def atr(df, n):
    h, l, c = df['High'], df['Low'], df['Close']
    tr = pd.concat([h-l, (h-c.shift(1)).abs(), (l-c.shift(1)).abs()], axis=1).max(axis=1)
    return tr.ewm(span=n, adjust=False).mean()

def adx_full(df, n):
    h, l, c = df['High'], df['Low'], df['Close']
    up  = h - h.shift(1)
    dn  = l.shift(1) - l
    pdm = pd.Series(np.where((up>dn)&(up>0), up, 0.), index=df.index)
    ndm = pd.Series(np.where((dn>up)&(dn>0), dn, 0.), index=df.index)
    a   = atr(df, n)
    pdi = 100 * pdm.ewm(span=n, adjust=False).mean() / a
    ndi = 100 * ndm.ewm(span=n, adjust=False).mean() / a
    dx  = (100*(pdi-ndi).abs()/(pdi+ndi).replace(0, np.nan)).fillna(0)
    return dx.ewm(span=n, adjust=False).mean(), pdi, ndi

def chop(df, n):
    h, l, c = df['High'], df['Low'], df['Close']
    tr = pd.concat([h-l,(h-c.shift(1)).abs(),(l-c.shift(1)).abs()], axis=1).max(axis=1)
    rng = (h.rolling(n).max() - l.rolling(n).min()).replace(0, np.nan)
    return 100 * np.log10(tr.rolling(n).sum() / rng) / np.log10(n)

print("Computing indicators...")
raw['ATR']   = atr(raw, ATR_N)
raw['ADX'], raw['PDI'], raw['NDI'] = adx_full(raw, ADX_N)
raw['CI']    = chop(raw, CI_N)
raw['EMA200']= raw['Close'].ewm(span=EMA_PERIOD, adjust=False).mean()
raw['DonHi'] = raw['High'].rolling(DONCHIAN_N).max().shift(1)
raw['DonLo'] = raw['Low'].rolling(DONCHIAN_N).min().shift(1)
raw = raw.dropna()
print(f"  After dropna: {len(raw)} bars")

# ─── ENGINE ──────────────────────────────────────────────────────────────────
balance  = INITIAL_BALANCE
peak_bal = INITIAL_BALANCE
log      = []

cur_day       = None
daily_start   = INITIAL_BALANCE
daily_blocked = False

def fresh():
    return dict(orb_hi=None, orb_lo=None, orb_done=False, traded=False)

sess_state = {s: fresh() for s in SESSIONS}
trade = None

def close_t(t, ep, ts, reason, partial=False, pvol=None):
    global balance, log
    vol = pvol if partial else t['vol']
    if vol <= 0: return
    mult = 1 if t['dir'] == 'long' else -1
    pnl  = (ep - t['entry']) * mult * vol * POINT_VALUE
    balance += pnl
    log.append({
        'entry_time': t['entry_time'], 'exit_time': ts,
        'session': t['session'], 'dir': t['dir'],
        'entry': t['entry'], 'exit': ep,
        'vol': vol, 'pnl': pnl, 'reason': reason, 'partial': partial,
        'adx': t.get('adx', 0), 'ci': t.get('ci', 0)
    })
    if partial:
        t['vol'] -= vol
    else:
        t['vol'] = 0

print("Running M15 backtest...")

for ts, row in raw.iterrows():
    price  = float(row['Close'])
    hi_    = float(row['High'])
    lo_    = float(row['Low'])
    atr_v  = float(row['ATR'])
    adx_v  = float(row['ADX'])
    ci_v   = float(row['CI'])
    pdi_v  = float(row['PDI'])
    ndi_v  = float(row['NDI'])
    ema_v  = float(row['EMA200'])
    don_hi = float(row['DonHi'])
    don_lo = float(row['DonLo'])
    hm     = (ts.hour, ts.minute)

    # ── Daily reset ───────────────────────────────────────────────────────
    bar_date = ts.date()
    if bar_date != cur_day:
        cur_day     = bar_date
        daily_start = balance
        daily_blocked = False
        for s in SESSIONS:
            sess_state[s] = fresh()

    if balance < daily_start * (1 - MAX_DAILY_DD):
        daily_blocked = True
        if trade:
            close_t(trade, price, ts, 'daily_dd')
            trade = None
        continue

    # ── Build ORB ─────────────────────────────────────────────────────────
    for sname, scfg in SESSIONS.items():
        st = sess_state[sname]
        if scfg['orb_start'] <= hm < scfg['orb_end']:
            st['orb_hi'] = max(st['orb_hi'], hi_) if st['orb_hi'] else hi_
            st['orb_lo'] = min(st['orb_lo'], lo_) if st['orb_lo'] else lo_
        if hm == scfg['orb_end'] and st['orb_hi'] and not st['orb_done']:
            st['orb_done'] = True

    # ── Manage trade ──────────────────────────────────────────────────────
    if trade and trade['vol'] > 0:
        t   = trade
        m   = 1 if t['dir'] == 'long' else -1
        pts = (price - t['entry']) * m
        pr  = t['pts_risk']

        # Partial TP at 1.5R
        if not t['partial_done'] and pts >= pr * PARTIAL_R:
            half = max(1, t['vol_full'] // 2)
            if half <= t['vol']:
                close_t(t, price, ts, 'partial_tp', partial=True, pvol=half)
                t['sl']          = t['entry'] + m
                t['be_done']     = True
                t['partial_done'] = True

        # Break-even at 1R (if no partial yet)
        if not t['be_done'] and pts >= pr:
            t['sl']     = t['entry'] + m
            t['be_done'] = True

        # ATR trailing
        if t['be_done'] and t['vol'] > 0:
            if t['dir'] == 'long':
                trail = price - atr_v * TRAIL_MULT
                t['trail_sl'] = max(t['sl'], trail) if t['trail_sl'] else trail
                t['sl'] = max(t['sl'], t['trail_sl'])
            else:
                trail = price + atr_v * TRAIL_MULT
                t['trail_sl'] = min(t['sl'], trail) if t['trail_sl'] else trail
                t['sl'] = min(t['sl'], t['trail_sl'])

        sc = SESSIONS[t['session']]['trade_until']
        sl_hit  = (t['dir']=='long' and lo_ <= t['sl']) or (t['dir']=='short' and hi_ >= t['sl'])
        don_hit = t['be_done'] and (
                  (t['dir']=='long'  and lo_ <= don_lo) or
                  (t['dir']=='short' and hi_ >= don_hi))
        eod = hm >= sc

        if sl_hit:
            close_t(t, t['sl'], ts, 'sl')
            trade = None
        elif don_hit:
            ep = don_lo if t['dir']=='long' else don_hi
            close_t(t, ep, ts, 'donchian')
            trade = None
        elif eod and t['vol'] > 0:
            close_t(t, price, ts, 'session_close')
            trade = None
        continue

    if daily_blocked:
        continue

    # ── Entry ─────────────────────────────────────────────────────────────
    for sname, scfg in SESSIONS.items():
        st = sess_state[sname]
        if not st['orb_done'] or st['traded']:
            continue
        if not (scfg['orb_end'] <= hm < scfg['trade_until']):
            continue
        if adx_v < ADX_MIN or ci_v > CI_MAX:
            continue
        orb_range = st['orb_hi'] - st['orb_lo']
        if orb_range < atr_v * ORB_QUALITY:
            continue

        pdi_ndi_diff = abs(pdi_v - ndi_v)
        if sname == 'london' and pdi_ndi_diff < LONDON_PDI_NDI:
            continue

        if price > st['orb_hi'] + SPREAD_PTS and price > ema_v and pdi_v > ndi_v:
            risk_p  = RISK_GRADE_A if adx_v > ADX_GRADE_A and ci_v < CI_GRADE_A else RISK_BASE
            sl_dist = atr_v * ATR_MULT_SL
            rpc     = sl_dist * POINT_VALUE
            vol     = max(1, int(balance * risk_p / rpc)) if rpc > 0 else 1
            trade   = {'dir':'long','entry':price,'sl':price-sl_dist,'vol':vol,'vol_full':vol,
                       'entry_time':ts,'be_done':False,'partial_done':False,'trail_sl':None,
                       'pts_risk':sl_dist,'session':sname,'adx':adx_v,'ci':ci_v}
            st['traded'] = True
            break

        elif price < st['orb_lo'] - SPREAD_PTS and price < ema_v and ndi_v > pdi_v:
            risk_p  = RISK_GRADE_A if adx_v > ADX_GRADE_A and ci_v < CI_GRADE_A else RISK_BASE
            sl_dist = atr_v * ATR_MULT_SL
            rpc     = sl_dist * POINT_VALUE
            vol     = max(1, int(balance * risk_p / rpc)) if rpc > 0 else 1
            trade   = {'dir':'short','entry':price,'sl':price+sl_dist,'vol':vol,'vol_full':vol,
                       'entry_time':ts,'be_done':False,'partial_done':False,'trail_sl':None,
                       'pts_risk':sl_dist,'session':sname,'adx':adx_v,'ci':ci_v}
            st['traded'] = True
            break

# ─── RESULTS ─────────────────────────────────────────────────────────────────
df_log = pd.DataFrame(log)
print()
print("=" * 65)
print("NAS100 ORB REGIME ENGINE v3 — M15 BACKTEST")
print("=" * 65)

if df_log.empty:
    print("No trades generated.")
else:
    wins   = df_log[df_log['pnl'] > 0]
    losses = df_log[df_log['pnl'] <= 0]
    wr     = len(wins) / len(df_log) * 100
    net    = df_log['pnl'].sum()
    avg_w  = wins['pnl'].mean()  if len(wins)   else 0
    avg_l  = losses['pnl'].mean() if len(losses) else 0
    rr     = abs(avg_w / avg_l)  if avg_l != 0  else 0

    run = INITIAL_BALANCE; pk = INITIAL_BALANCE; max_dd = 0
    for p in df_log.sort_values('exit_time')['pnl']:
        run += p; pk = max(pk, run)
        max_dd = max(max_dd, (pk - run) / pk)

    # Monthly grouping
    df_log['month'] = df_log['exit_time'].dt.to_period('M')
    monthly = df_log.groupby('month')['pnl'].sum()

    full_trades  = df_log[df_log['reason'] != 'partial_tp']
    partial_tps  = df_log[df_log['reason'] == 'partial_tp']

    print(f"Period:            {df_log['entry_time'].min().date()} → {df_log['exit_time'].max().date()}")
    print(f"Total bar count:   {len(raw)} M15 bars")
    print(f"Trade events:      {len(df_log)}  (full={len(full_trades)}, partial_tp={len(partial_tps)})")
    print(f"Unique trades:     {len(full_trades)}")
    print(f"Win rate (all):    {wr:.1f}%")
    print(f"Win rate (full):   {len(full_trades[full_trades['pnl']>0])/len(full_trades)*100:.1f}%")
    print(f"Avg win:          ${avg_w:,.0f}")
    print(f"Avg loss:         ${avg_l:,.0f}")
    print(f"Reward/Risk:       {rr:.2f}")
    print(f"Net P&L:          ${net:,.0f}  ({net/INITIAL_BALANCE*100:.2f}%)")
    print(f"Final balance:    ${balance:,.0f}")
    print(f"Max drawdown:      {max_dd*100:.2f}%")
    print(f"Trades/month avg:  {len(full_trades) / (len(monthly)):.1f}")
    print()
    print("Monthly P&L breakdown:")
    for m, p in monthly.items():
        bar = "+" * int(abs(p)/200) if p>0 else "-" * int(abs(p)/200)
        print(f"  {m}  ${p:>8,.0f}  {bar}")
    print()
    print("Exit breakdown:")
    print(df_log['reason'].value_counts().to_string())
    print()
    print("Direction breakdown:")
    print(df_log.groupby('dir')['pnl'].agg(['count','sum','mean']).to_string())
    print()
    print("All trades:")
    disp = df_log[['entry_time','exit_time','dir','entry','exit','pnl','reason','adx','ci']].copy()
    disp['entry_time'] = disp['entry_time'].dt.strftime('%m-%d %H:%M')
    disp['exit_time']  = disp['exit_time'].dt.strftime('%m-%d %H:%M')
    disp['pnl']   = disp['pnl'].map('${:,.0f}'.format)
    disp['entry'] = disp['entry'].map('{:.1f}'.format)
    disp['exit']  = disp['exit'].map('{:.1f}'.format)
    disp['adx']   = disp['adx'].map('{:.1f}'.format)
    disp['ci']    = disp['ci'].map('{:.1f}'.format)
    print(disp.to_string(index=False))

print()
print("Data: NQ=F CME NAS100 Futures, 15-minute bars, yfinance")
