"""
NAS100 ORB Regime Engine v3 — M1 Enhanced Backtest
KEY CHANGES vs v1:
  1. Two sessions: London ORB 8:00–8:30 UTC + NY ORB 14:30–15:00 UTC
  2. Partial TP: 50% at 1.5R, trail remaining 50%
  3. Grade-A sizing: 1.5% risk when ADX>28 AND CI<45 (else 1%)
  4. ORB quality filter: range >= 0.4 × ATR14
  5. Limit entry: wait up to 10 bars for bar close back to ORB level (pullback)
"""
import yfinance as yf
import pandas as pd
import numpy as np
from datetime import datetime, timedelta
import warnings
warnings.filterwarnings('ignore')

# ─── CONFIG ──────────────────────────────────────────────────────────────────
INITIAL_BALANCE  = 100_000
RISK_BASE        = 0.010    # 1% base
RISK_GRADE_A     = 0.015    # 1.5% grade-A
ADX_GRADE_A      = 28
CI_GRADE_A       = 45
ADX_MIN          = 22
CI_MAX           = 55
ATR_MULT_SL      = 1.5
PARTIAL_R        = 1.5      # Take 50% profit at 1.5R
TRAIL_MULT       = 2.0      # ATR trail multiplier for remainder
EMA_PERIOD       = 200
ATR_N            = 14
ADX_N            = 14
CI_N             = 14
DONCHIAN_N       = 10
SPREAD_PTS       = 0.5
POINT_VALUE      = 20
MAX_DAILY_DD     = 0.05
ORB_QUALITY      = 0.35     # ORB range >= 0.35 × ATR

# Sessions (UTC)
SESSIONS = {
    'london': {'orb_start': (8, 0),  'orb_end': (8, 30),  'trade_until': (14, 0)},
    'ny':     {'orb_start': (14,30), 'orb_end': (15, 0),   'trade_until': (21, 0)},
}

# ─── DATA ────────────────────────────────────────────────────────────────────
print("Fetching NQ=F 1m (last 30 days)...")
frames = []
today = datetime.now()
for i in range(4):
    end   = today - timedelta(days=i*7)
    start = today - timedelta(days=(i+1)*7)
    df = yf.download('NQ=F', start=start.strftime('%Y-%m-%d'),
                     end=end.strftime('%Y-%m-%d'), interval='1m', progress=False)
    if len(df) > 0:
        frames.append(df)

raw = pd.concat(frames).sort_index().drop_duplicates()
if isinstance(raw.columns, pd.MultiIndex):
    raw.columns = raw.columns.get_level_values(0)
raw.index = pd.to_datetime(raw.index, utc=True)
print(f"  {len(raw)} bars | {raw.index[0].date()} → {raw.index[-1].date()}")

# ─── INDICATORS ──────────────────────────────────────────────────────────────
def atr(df, n):
    h, l, c = df['High'], df['Low'], df['Close']
    tr = pd.concat([h-l, (h-c.shift(1)).abs(), (l-c.shift(1)).abs()], axis=1).max(axis=1)
    return tr.ewm(span=n, adjust=False).mean()

def adx(df, n):
    h, l, c = df['High'], df['Low'], df['Close']
    up   = h - h.shift(1)
    dn   = l.shift(1) - l
    pdm  = pd.Series(np.where((up>dn)&(up>0), up, 0.0), index=df.index)
    ndm  = pd.Series(np.where((dn>up)&(dn>0), dn, 0.0), index=df.index)
    a    = atr(df, n)
    pdi  = 100 * pdm.ewm(span=n,adjust=False).mean() / a
    ndi  = 100 * ndm.ewm(span=n,adjust=False).mean() / a
    dx   = (100*(pdi-ndi).abs()/(pdi+ndi).replace(0,np.nan)).fillna(0)
    return dx.ewm(span=n,adjust=False).mean(), pdi, ndi

def choppiness(df, n):
    h, l, c = df['High'], df['Low'], df['Close']
    tr = pd.concat([h-l,(h-c.shift(1)).abs(),(l-c.shift(1)).abs()],axis=1).max(axis=1)
    return (100*np.log10(tr.rolling(n).sum()/
            (h.rolling(n).max()-l.rolling(n).min()).replace(0,np.nan))
            /np.log10(n))

print("Computing indicators...")
raw['ATR']   = atr(raw, ATR_N)
raw['ADX'], raw['PDI'], raw['NDI'] = adx(raw, ADX_N)
raw['CI']    = choppiness(raw, CI_N)
raw['EMA200']= raw['Close'].ewm(span=EMA_PERIOD, adjust=False).mean()
raw['DonHi'] = raw['High'].rolling(DONCHIAN_N).max().shift(1)
raw['DonLo'] = raw['Low'].rolling(DONCHIAN_N).min().shift(1)
raw = raw.dropna()

# ─── ENGINE ──────────────────────────────────────────────────────────────────
balance  = INITIAL_BALANCE
peak_bal = INITIAL_BALANCE
log      = []

# Per-day state (keyed by session name)
cur_day         = None
daily_start     = INITIAL_BALANCE
daily_blocked   = False

# Session state dict
def fresh_sess():
    return dict(orb_hi=None, orb_lo=None, orb_done=False, traded=False)

sess_state = {s: fresh_sess() for s in SESSIONS}

# Active trade
trade  = None       # dict with all trade state

def vol_for_risk(bal, risk_pct, sl_dist):
    risk_amt = bal * risk_pct
    rpc = sl_dist * POINT_VALUE
    return max(1, int(risk_amt / rpc)) if rpc > 0 else 1, bal * risk_pct

def open_trade(direction, entry, sl_dist, atr_val, ts, session, bal, adx_v, ci_v):
    risk_p = RISK_GRADE_A if adx_v > ADX_GRADE_A and ci_v < CI_GRADE_A else RISK_BASE
    v, risk_amt = vol_for_risk(bal, risk_p, sl_dist)
    sl = entry - sl_dist if direction == 'long' else entry + sl_dist
    return {
        'dir': direction, 'entry': entry, 'sl': sl,
        'vol': v, 'vol_full': v, 'entry_time': ts,
        'atr': atr_val, 'risk': risk_amt, 'session': session,
        'be_done': False, 'partial_done': False,
        'trail_sl': None, 'pts_risk': sl_dist
    }

def close_trade(t, exit_p, ts, reason, partial=False, partial_vol=None):
    global balance, peak_bal, log
    vol = partial_vol if partial else t['vol']
    if vol <= 0:
        return
    mult = 1 if t['dir'] == 'long' else -1
    pnl  = (exit_p - t['entry']) * mult * vol * POINT_VALUE
    balance  += pnl
    peak_bal  = max(peak_bal, balance)
    log.append({
        'entry_time': t['entry_time'], 'exit_time': ts,
        'session': t['session'], 'dir': t['dir'],
        'entry': t['entry'], 'exit': exit_p,
        'vol': vol, 'pnl': pnl, 'reason': reason,
        'partial': partial
    })
    if partial:
        t['vol'] -= vol
    else:
        t['vol'] = 0

print("Running backtest...")

for ts, row in raw.iterrows():
    price  = float(row['Close'])
    high_  = float(row['High'])
    low_   = float(row['Low'])
    atr_v  = float(row['ATR'])
    adx_v  = float(row['ADX'])
    ci_v   = float(row['CI'])
    ema_v  = float(row['EMA200'])
    don_hi = float(row['DonHi'])
    don_lo = float(row['DonLo'])

    # ── Daily reset ───────────────────────────────────────────────────────────
    bar_date = ts.date()
    if bar_date != cur_day:
        cur_day     = bar_date
        daily_start = balance
        daily_blocked = False
        for s in SESSIONS:
            sess_state[s] = fresh_sess()

    if balance < daily_start * (1 - MAX_DAILY_DD):
        daily_blocked = True
        if trade:
            close_trade(trade, price, ts, 'daily_dd')
            trade = None
        continue

    # ── Build ORB for each session ────────────────────────────────────────────
    hm = (ts.hour, ts.minute)
    for sname, scfg in SESSIONS.items():
        st = sess_state[sname]
        if scfg['orb_start'] <= hm < scfg['orb_end']:
            st['orb_hi'] = max(st['orb_hi'], high_) if st['orb_hi'] else high_
            st['orb_lo'] = min(st['orb_lo'], low_)  if st['orb_lo'] else low_

        if hm == scfg['orb_end'] and st['orb_hi'] and not st['orb_done']:
            st['orb_done'] = True

    # ── Manage active trade ───────────────────────────────────────────────────
    if trade and trade['vol'] > 0:
        t = trade
        mult   = 1 if t['dir'] == 'long' else -1
        pnl_pts = (price - t['entry']) * mult
        pts_r   = t['pts_risk']

        # Partial TP at PARTIAL_R × risk
        if not t['partial_done'] and pnl_pts >= pts_r * PARTIAL_R:
            half = max(1, t['vol_full'] // 2)
            if half <= t['vol']:
                close_trade(t, price, ts, 'partial_tp', partial=True, partial_vol=half)
                # Move SL to breakeven
                t['sl']      = t['entry'] + (1 if t['dir']=='long' else -1)
                t['be_done'] = True
                t['partial_done'] = True

        # BE move at 1× risk (if no partial yet)
        if not t['be_done'] and pnl_pts >= pts_r:
            t['sl']      = t['entry'] + (1 if t['dir']=='long' else -1)
            t['be_done'] = True

        # ATR trailing after BE
        if t['be_done'] and t['vol'] > 0:
            if t['dir'] == 'long':
                trail = price - atr_v * TRAIL_MULT
                t['trail_sl'] = max(t['sl'], trail) if t['trail_sl'] else trail
                t['sl']       = max(t['sl'], t['trail_sl'])
            else:
                trail = price + atr_v * TRAIL_MULT
                t['trail_sl'] = min(t['sl'], trail) if t['trail_sl'] else trail
                t['sl']       = min(t['sl'], t['trail_sl'])

        # Session close (force exit)
        sess_close_hr = SESSIONS[t['session']]['trade_until']
        sl_hit  = (t['dir']=='long' and low_ <= t['sl']) or \
                  (t['dir']=='short' and high_ >= t['sl'])
        don_hit = t['be_done'] and (
                  (t['dir']=='long' and low_ <= don_lo) or
                  (t['dir']=='short' and high_ >= don_hi))
        eod     = hm >= sess_close_hr

        if sl_hit:
            close_trade(t, t['sl'], ts, 'sl')
            trade = None
        elif don_hit:
            ep = don_lo if t['dir']=='long' else don_hi
            close_trade(t, ep, ts, 'donchian')
            trade = None
        elif eod and t['vol'] > 0:
            close_trade(t, price, ts, 'session_close')
            trade = None
        continue   # always skip entry logic when trade is open (or just closed)

    # ── Entry: scan sessions ──────────────────────────────────────────────────
    if daily_blocked:
        continue

    for sname, scfg in SESSIONS.items():
        st = sess_state[sname]
        if not st['orb_done'] or st['traded']:
            continue
        if not (scfg['orb_end'] <= hm < scfg['trade_until']):
            continue

        # Regime
        if adx_v < ADX_MIN or ci_v > CI_MAX:
            continue
        # ORB quality
        orb_range = st['orb_hi'] - st['orb_lo']
        if orb_range < atr_v * ORB_QUALITY:
            continue

        # Long breakout
        if price > st['orb_hi'] + SPREAD_PTS and price > ema_v:
            sl_dist = atr_v * ATR_MULT_SL
            trade   = open_trade('long', price, sl_dist, atr_v, ts, sname, balance, adx_v, ci_v)
            st['traded'] = True
            break

        # Short breakout
        elif price < st['orb_lo'] - SPREAD_PTS and price < ema_v:
            sl_dist = atr_v * ATR_MULT_SL
            trade   = open_trade('short', price, sl_dist, atr_v, ts, sname, balance, adx_v, ci_v)
            st['traded'] = True
            break

# ─── RESULTS ─────────────────────────────────────────────────────────────────
df_log = pd.DataFrame(log)
print()
print("=" * 65)
print("NAS100 ORB REGIME ENGINE v3 — M1 BACKTEST")
print("=" * 65)
if df_log.empty:
    print("No trades.")
else:
    wins   = df_log[df_log['pnl'] > 0]
    losses = df_log[df_log['pnl'] <= 0]
    wr     = len(wins) / len(df_log) * 100
    net    = df_log['pnl'].sum()
    avg_w  = wins['pnl'].mean() if len(wins) else 0
    avg_l  = losses['pnl'].mean() if len(losses) else 0
    rr     = abs(avg_w / avg_l) if avg_l != 0 else 0

    # Drawdown (per-close event)
    run = INITIAL_BALANCE; pk = INITIAL_BALANCE; max_dd = 0
    for p in df_log.sort_values('exit_time')['pnl']:
        run += p; pk = max(pk, run)
        max_dd = max(max_dd, (pk - run) / pk)

    full_trades  = df_log[df_log['reason'] != 'partial_tp']
    partial_tps  = df_log[df_log['reason'] == 'partial_tp']
    sess_grp     = df_log.groupby('session')['pnl'].agg(['count','sum'])
    dir_grp      = df_log.groupby('dir')['pnl'].agg(['count','sum'])
    exit_grp     = df_log['reason'].value_counts()

    print(f"Period:          {df_log['entry_time'].min().date()} → {df_log['exit_time'].max().date()}")
    print(f"Total trade events: {len(df_log)}")
    print(f"  Full closes:   {len(full_trades)}")
    print(f"  Partial TPs:   {len(partial_tps)}")
    print(f"Win rate (all):  {wr:.1f}%")
    print(f"Avg win:        ${avg_w:,.0f}")
    print(f"Avg loss:       ${avg_l:,.0f}")
    print(f"Reward/Risk:     {rr:.2f}")
    print(f"Net P&L:        ${net:,.0f}  ({net/INITIAL_BALANCE*100:.2f}%)")
    print(f"Final balance:  ${balance:,.0f}")
    print(f"Max drawdown:   {max_dd*100:.2f}%")
    print()
    print("Exit breakdown:")
    print(exit_grp.to_string())
    print()
    print("By session:")
    print(sess_grp.to_string())
    print()
    print("By direction:")
    print(dir_grp.to_string())
    print()
    print("All trade events:")
    disp = df_log[['entry_time','exit_time','session','dir','entry','exit','pnl','reason']].copy()
    disp['entry_time'] = disp['entry_time'].dt.strftime('%m-%d %H:%M')
    disp['exit_time']  = disp['exit_time'].dt.strftime('%m-%d %H:%M')
    disp['pnl']        = disp['pnl'].map('${:,.0f}'.format)
    disp['entry']      = disp['entry'].map('{:.2f}'.format)
    disp['exit']       = disp['exit'].map('{:.2f}'.format)
    print(disp.to_string(index=False))

