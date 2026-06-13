"""
NAS100 ORB Regime Engine — M1 Backtest (30-day NQ=F)
Strategy: 14:30–15:00 UTC Opening Range Breakout on M1 bars
"""
import yfinance as yf
import pandas as pd
import numpy as np
from datetime import datetime, timedelta, timezone
import warnings
warnings.filterwarnings('ignore')

# ─── CONFIG ──────────────────────────────────────────────────────────────────
INITIAL_BALANCE  = 100_000
RISK_PCT         = 0.01          # 1% risk per trade
ATR_MULT_SL      = 1.5           # SL = ATR × 1.5
ATR_MULT_BE      = 1.0           # Breakeven trigger = 1R
ATR_TRAIL_MULT   = 2.0           # ATR trailing stop multiplier
ADX_THRESHOLD    = 22
CI_THRESHOLD     = 55
EMA_PERIOD       = 200
ATR_N            = 14
ADX_N            = 14
CI_N             = 14
DONCHIAN_N       = 10            # Donchian exit channel
MIN_SPREAD_PIPS  = 0.5           # Simulated spread
POINT_VALUE      = 20            # NQ=F: $20 per point per contract
ORB_START_UTC    = 14            # 14:30 UTC = NY 9:30 + 30min buffer (use 1H proxy: 14:00)
ORB_END_UTC      = 15            # ORB complete at 15:00 UTC
SESSION_CLOSE    = 21            # Hard close at 21:00 UTC (16:00 NY)
MAX_DAILY_DD     = 0.05          # 5% daily drawdown limit

# ─── FETCH DATA ──────────────────────────────────────────────────────────────
print("Fetching NQ=F 1m data (last 30 days)...")
frames = []
today = datetime.now()
for i in range(4):  # 4 × 7-day windows = 28 days
    end   = today - timedelta(days=i*7)
    start = today - timedelta(days=(i+1)*7)
    df = yf.download('NQ=F', start=start.strftime('%Y-%m-%d'),
                     end=end.strftime('%Y-%m-%d'), interval='1m', progress=False)
    if len(df) > 0:
        frames.append(df)

raw = pd.concat(frames).sort_index().drop_duplicates()
# Flatten MultiIndex columns if present
if isinstance(raw.columns, pd.MultiIndex):
    raw.columns = raw.columns.get_level_values(0)
raw.index = pd.to_datetime(raw.index, utc=True)
print(f"Raw data: {len(raw)} bars | {raw.index[0]} → {raw.index[-1]}")

# ─── INDICATORS ──────────────────────────────────────────────────────────────
def compute_atr(df, n=14):
    h, l, c = df['High'], df['Low'], df['Close']
    pc = c.shift(1)
    tr = pd.concat([h-l, (h-pc).abs(), (l-pc).abs()], axis=1).max(axis=1)
    return tr.ewm(span=n, adjust=False).mean()

def compute_adx(df, n=14):
    h, l, c = df['High'], df['Low'], df['Close']
    up   = h - h.shift(1)
    down = l.shift(1) - l
    pdm  = np.where((up > down) & (up > 0), up, 0.0)
    ndm  = np.where((down > up) & (down > 0), down, 0.0)
    atr  = compute_atr(df, n)
    pdi  = 100 * pd.Series(pdm, index=df.index).ewm(span=n, adjust=False).mean() / atr
    ndi  = 100 * pd.Series(ndm, index=df.index).ewm(span=n, adjust=False).mean() / atr
    dx   = (100 * (pdi - ndi).abs() / (pdi + ndi).replace(0, np.nan)).fillna(0)
    return dx.ewm(span=n, adjust=False).mean(), pdi, ndi

def compute_choppiness(df, n=14):
    h, l, c = df['High'], df['Low'], df['Close']
    pc = c.shift(1)
    tr = pd.concat([h-l, (h-pc).abs(), (l-pc).abs()], axis=1).max(axis=1)
    atr_sum = tr.rolling(n).sum()
    hh      = h.rolling(n).max()
    ll      = l.rolling(n).min()
    denom   = (hh - ll).replace(0, np.nan)
    ci      = 100 * np.log10(atr_sum / denom) / np.log10(n)
    return ci

print("Computing indicators...")
raw['ATR']  = compute_atr(raw, ATR_N)
raw['ADX'], raw['PDI'], raw['NDI'] = compute_adx(raw, ADX_N)
raw['CI']   = compute_choppiness(raw, CI_N)
raw['EMA200'] = raw['Close'].ewm(span=EMA_PERIOD, adjust=False).mean()
raw['DonHi']  = raw['High'].rolling(DONCHIAN_N).max().shift(1)
raw['DonLo']  = raw['Low'].rolling(DONCHIAN_N).min().shift(1)
raw = raw.dropna()

# ─── BACKTEST ENGINE ─────────────────────────────────────────────────────────
class Trade:
    def __init__(self, direction, entry, sl, tp, volume, entry_time, atr):
        self.direction  = direction   # 'long' or 'short'
        self.entry      = entry
        self.sl         = sl
        self.tp         = tp          # Donchian-based or None
        self.volume     = volume      # contracts
        self.entry_time = entry_time
        self.atr        = atr
        self.be_moved   = False
        self.trail_sl   = None
        self.risk_amt   = 0

balance       = INITIAL_BALANCE
peak_balance  = INITIAL_BALANCE
trades_log    = []
active_trade  = None
orb_high      = None
orb_low       = None
orb_complete  = False
daily_start_balance = INITIAL_BALANCE
current_day   = None
daily_blocked = False

print("Running M1 backtest...")

for i, (ts, row) in enumerate(raw.iterrows()):
    utc_hour = ts.hour
    utc_min  = ts.minute
    price    = float(row['Close'])
    high     = float(row['High'])
    low      = float(row['Low'])
    atr      = float(row['ATR'])
    adx      = float(row['ADX'])
    ci       = float(row['CI'])
    ema200   = float(row['EMA200'])
    don_hi   = float(row['DonHi'])
    don_lo   = float(row['DonLo'])

    # Daily reset
    bar_date = ts.date()
    if bar_date != current_day:
        current_day         = bar_date
        daily_start_balance = balance
        daily_blocked       = False
        orb_high            = None
        orb_low             = None
        orb_complete        = False

    # Daily DD check
    if balance < daily_start_balance * (1 - MAX_DAILY_DD):
        daily_blocked = True
        if active_trade:
            pnl = (price - active_trade.entry) * (1 if active_trade.direction=='long' else -1) * active_trade.volume * POINT_VALUE
            balance += pnl - active_trade.risk_amt * 0.001  # spread cost
            trades_log.append({
                'entry_time': active_trade.entry_time, 'exit_time': ts,
                'direction': active_trade.direction, 'entry': active_trade.entry,
                'exit': price, 'pnl': pnl, 'exit_reason': 'daily_dd_stop'
            })
            active_trade = None
        continue

    # ─── Build ORB (14:30–15:00 UTC) ─────────────────────────────────────────
    if utc_hour == 14 and utc_min >= 30:
        if orb_high is None:
            orb_high = high
            orb_low  = low
        else:
            orb_high = max(orb_high, high)
            orb_low  = min(orb_low, low)
    elif utc_hour == 15 and utc_min == 0 and orb_high is not None and not orb_complete:
        orb_complete = True

    # ─── Manage active trade ─────────────────────────────────────────────────
    if active_trade:
        t = active_trade
        entry_pnl = price - t.entry if t.direction == 'long' else t.entry - price
        pts_risk  = abs(t.entry - t.sl)

        # Breakeven trigger
        if not t.be_moved and entry_pnl >= pts_risk * ATR_MULT_BE:
            if t.direction == 'long':
                t.sl = t.entry + 1
            else:
                t.sl = t.entry - 1
            t.be_moved = True

        # ATR trailing stop after breakeven
        if t.be_moved:
            if t.direction == 'long':
                new_trail = price - atr * ATR_TRAIL_MULT
                t.trail_sl = max(t.sl, new_trail) if t.trail_sl else new_trail
                t.sl = max(t.sl, t.trail_sl)
            else:
                new_trail = price + atr * ATR_TRAIL_MULT
                t.trail_sl = min(t.sl, new_trail) if t.trail_sl else new_trail
                t.sl = min(t.sl, t.trail_sl)

        # Check SL
        sl_hit = (t.direction == 'long' and low <= t.sl) or (t.direction == 'short' and high >= t.sl)
        # Check Donchian exit
        don_hit = (t.direction == 'long' and low <= don_lo) or (t.direction == 'short' and high >= don_hi)
        # Session close
        sess_close = (utc_hour >= SESSION_CLOSE)

        exit_price  = None
        exit_reason = None
        if sl_hit:
            exit_price  = t.sl
            exit_reason = 'sl'
        elif don_hit and t.be_moved:
            exit_price  = don_lo if t.direction == 'long' else don_hi
            exit_reason = 'donchian'
        elif sess_close:
            exit_price  = price
            exit_reason = 'session_close'

        if exit_price:
            pnl = (exit_price - t.entry) * (1 if t.direction == 'long' else -1) * t.volume * POINT_VALUE
            balance += pnl
            peak_balance = max(peak_balance, balance)
            duration_min = (ts - t.entry_time).total_seconds() / 60
            trades_log.append({
                'entry_time': t.entry_time, 'exit_time': ts,
                'direction': t.direction, 'entry': t.entry, 'exit': exit_price,
                'pnl': pnl, 'exit_reason': exit_reason, 'duration_min': duration_min,
                'atr': t.atr, 'volume': t.volume
            })
            active_trade = None
            continue

    # ─── Entry logic: ORB breakout on M1 bar close (15:00–21:00 UTC) ─────────
    if (active_trade is None and orb_complete and orb_high and orb_low
            and not daily_blocked and SESSION_CLOSE > utc_hour >= 15):

        # Regime filter
        regime_ok = adx >= ADX_THRESHOLD and ci <= CI_THRESHOLD

        if regime_ok:
            orb_range = orb_high - orb_low
            if orb_range < 2:  # minimum range filter (2 NQ points)
                pass
            else:
                # Long signal: close above ORB high
                if price > orb_high + MIN_SPREAD_PIPS and price > ema200:
                    sl_dist = atr * ATR_MULT_SL
                    sl = price - sl_dist
                    risk_per_contract = sl_dist * POINT_VALUE
                    if risk_per_contract > 0:
                        risk_amt = balance * RISK_PCT
                        volume   = max(1, int(risk_amt / risk_per_contract))
                        t = Trade('long', price, sl, None, volume, ts, atr)
                        t.risk_amt = risk_amt
                        active_trade = t
                        orb_complete = False  # one trade per day
                # Short signal: close below ORB low
                elif price < orb_low - MIN_SPREAD_PIPS and price < ema200:
                    sl_dist = atr * ATR_MULT_SL
                    sl = price + sl_dist
                    risk_per_contract = sl_dist * POINT_VALUE
                    if risk_per_contract > 0:
                        risk_amt = balance * RISK_PCT
                        volume   = max(1, int(risk_amt / risk_per_contract))
                        t = Trade('short', price, sl, None, volume, ts, atr)
                        t.risk_amt = risk_amt
                        active_trade = t
                        orb_complete = False

# ─── RESULTS ─────────────────────────────────────────────────────────────────
results = pd.DataFrame(trades_log)
print()
print("=" * 60)
print("NAS100 ORB REGIME ENGINE — M1 BACKTEST (30 DAYS)")
print("=" * 60)
if len(results) == 0:
    print("No trades generated.")
else:
    wins    = results[results['pnl'] > 0]
    losses  = results[results['pnl'] <= 0]
    wr      = len(wins) / len(results) * 100
    avg_win = wins['pnl'].mean() if len(wins) else 0
    avg_los = losses['pnl'].mean() if len(losses) else 0
    rr      = abs(avg_win / avg_los) if avg_los != 0 else 0
    net_pnl = results['pnl'].sum()

    # Max drawdown
    running = INITIAL_BALANCE
    peak    = INITIAL_BALANCE
    max_dd  = 0
    for pnl in results['pnl']:
        running += pnl
        peak = max(peak, running)
        dd = (peak - running) / peak
        max_dd = max(max_dd, dd)

    exit_breakdown = results['exit_reason'].value_counts()

    print(f"Period:          {results['entry_time'].min().date()} → {results['exit_time'].max().date()}")
    print(f"Trades:          {len(results)}")
    print(f"Win rate:        {wr:.1f}%")
    print(f"Avg win:        ${avg_win:,.0f}")
    print(f"Avg loss:       ${avg_los:,.0f}")
    print(f"Reward/Risk:     {rr:.2f}")
    print(f"Net P&L:        ${net_pnl:,.0f}  ({net_pnl/INITIAL_BALANCE*100:.2f}%)")
    print(f"Final balance:  ${balance:,.0f}")
    print(f"Max drawdown:   {max_dd*100:.2f}%")
    print()
    print("Exit breakdown:")
    print(exit_breakdown.to_string())
    print()
    print("Trade log (last 15):")
    print(results[['entry_time','direction','entry','exit','pnl','exit_reason']].tail(15).to_string())

print()
print("Data: NQ=F (CME NAS100 Futures) 1-minute bars")
print("Timeframe: last 30 days from yfinance")
