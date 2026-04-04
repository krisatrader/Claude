"""
XAUUSD M15 Breakout & Retest Strategy - Python Backtesting
===========================================================
Requirements:
    pip install backtesting pandas numpy yfinance ta

Target  : ~5% monthly profit
Max SL  : 1% account risk per trade
Trades  : Max 1 per day
"""

import numpy as np
import pandas as pd
from backtesting import Backtest, Strategy
from backtesting.lib import crossover
import warnings
warnings.filterwarnings("ignore")

# ---------------------------------------------------------------------------
# Strategy parameters (mirror the MQL5 EA)
# ---------------------------------------------------------------------------
SWING_LOOKBACK       = 20     # Candles to look back for swing H/L
MAX_RETEST_CANDLES   = 6      # Max candles to wait for retest
RETEST_ZONE_PCT      = 0.15   # Retest zone tolerance (fraction of ATR)
MIN_BREAKOUT_POINTS  = 1.5    # Minimum breakout in dollars (XAUUSD)
RISK_REWARD          = 2.5    # TP = SL * RR
ATR_PERIOD           = 14     # ATR period
TREND_EMA_PERIOD     = 50     # EMA for trend filter on same TF (H1 approx = 4*M15)
MAX_RISK_PCT         = 1.0    # Max % of equity risked per trade
SESSION_START        = 7      # UTC hour
SESSION_END          = 18     # UTC hour


# ---------------------------------------------------------------------------
# Helper: rolling ATR
# ---------------------------------------------------------------------------
def compute_atr(high: pd.Series, low: pd.Series, close: pd.Series, period: int) -> pd.Series:
    tr = pd.concat([
        high - low,
        (high - close.shift(1)).abs(),
        (low  - close.shift(1)).abs()
    ], axis=1).max(axis=1)
    return tr.rolling(period).mean()


# ---------------------------------------------------------------------------
# Backtesting.py Strategy class
# ---------------------------------------------------------------------------
class XAUUSDBreakoutRetest(Strategy):
    # Expose as optimisable parameters
    swing_lookback      = SWING_LOOKBACK
    max_retest_candles  = MAX_RETEST_CANDLES
    retest_zone_pct     = RETEST_ZONE_PCT
    min_breakout_pts    = MIN_BREAKOUT_POINTS
    risk_reward         = RISK_REWARD
    atr_period          = ATR_PERIOD
    trend_ema_period    = TREND_EMA_PERIOD
    max_risk_pct        = MAX_RISK_PCT
    session_start       = SESSION_START
    session_end         = SESSION_END

    def init(self):
        close = pd.Series(self.data.Close)
        high  = pd.Series(self.data.High)
        low   = pd.Series(self.data.Low)

        self.atr = self.I(
            lambda h, l, c: compute_atr(pd.Series(h), pd.Series(l), pd.Series(c), self.atr_period),
            self.data.High, self.data.Low, self.data.Close,
            name="ATR"
        )
        self.ema_trend = self.I(
            lambda c: pd.Series(c).ewm(span=self.trend_ema_period, adjust=False).mean(),
            self.data.Close,
            name="EMA_Trend"
        )

        # State machine
        self._breakout_dir    = 0      # 1 buy, -1 sell
        self._breakout_level  = 0.0
        self._retest_count    = 0
        self._waiting_retest  = False
        self._atr_at_breakout = 0.0
        self._last_trade_day  = None

    def next(self):
        i = len(self.data) - 1  # current bar index

        # ---- Already in a trade ------------------------------------------------
        if self.position:
            return

        # ---- One trade per day -------------------------------------------------
        current_time = self.data.index[i]
        current_day  = current_time.date() if hasattr(current_time, 'date') else None
        if self._last_trade_day is not None and current_day == self._last_trade_day:
            return

        # ---- Session filter ----------------------------------------------------
        if hasattr(current_time, 'hour'):
            if not (self.session_start <= current_time.hour < self.session_end):
                return

        if i < self.swing_lookback + 2:
            return

        # ---- ATR ---------------------------------------------------------------
        atr = self.atr[i]
        if np.isnan(atr) or atr <= 0:
            return

        # ---- Trend filter (EMA on same bar) ------------------------------------
        ema = self.ema_trend[i]
        trend_dir = 1 if self.data.Close[i] > ema else -1

        # ---- Swing high / low (exclude last 2 bars: index i and i-1) ----------
        lookback_highs = self.data.High[i - self.swing_lookback - 1 : i - 1]
        lookback_lows  = self.data.Low[i  - self.swing_lookback - 1 : i - 1]
        swing_high = float(np.max(lookback_highs))
        swing_low  = float(np.min(lookback_lows))

        # Previous closed candle
        prev_close = self.data.Close[i - 1]
        prev_open  = self.data.Open[i - 1]
        prev_high  = self.data.High[i - 1]
        prev_low   = self.data.Low[i - 1]

        # ===== STATE: Not waiting for retest → detect breakout ==================
        if not self._waiting_retest:
            min_bo = self.min_breakout_pts

            # Bullish breakout
            if prev_close > swing_high + min_bo and trend_dir == 1:
                self._breakout_dir    = 1
                self._breakout_level  = swing_high
                self._retest_count    = 0
                self._waiting_retest  = True
                self._atr_at_breakout = atr

            # Bearish breakout
            elif prev_close < swing_low - min_bo and trend_dir == -1:
                self._breakout_dir    = -1
                self._breakout_level  = swing_low
                self._retest_count    = 0
                self._waiting_retest  = True
                self._atr_at_breakout = atr

        # ===== STATE: Waiting for retest ========================================
        else:
            self._retest_count += 1

            if self._retest_count > self.max_retest_candles:
                self._waiting_retest = False
                self._breakout_dir   = 0
                return

            zone = self._atr_at_breakout * self.retest_zone_pct
            zone_upper = self._breakout_level + zone
            zone_lower = self._breakout_level - zone

            # ---- Bullish retest ------------------------------------------------
            if self._breakout_dir == 1:
                price_touched_zone = prev_low <= zone_upper and prev_high >= zone_lower
                bullish_confirm    = (prev_close > prev_open and
                                      (prev_close - prev_open) > 0.3 * (prev_high - prev_low))

                if price_touched_zone and bullish_confirm:
                    entry  = self.data.Close[i]        # market order on next open
                    sl_dist = max(entry - prev_low + atr * 0.25, atr * 0.5)
                    tp_dist = sl_dist * self.risk_reward
                    sl = entry - sl_dist
                    tp = entry + tp_dist

                    size = self._calc_size(entry, sl)
                    if size > 0:
                        self.buy(sl=sl, tp=tp, size=size)
                        self._last_trade_day  = current_day
                        self._waiting_retest  = False
                        self._breakout_dir    = 0

            # ---- Bearish retest ------------------------------------------------
            elif self._breakout_dir == -1:
                price_touched_zone = prev_high >= zone_lower and prev_low <= zone_upper
                bearish_confirm    = (prev_close < prev_open and
                                      (prev_open - prev_close) > 0.3 * (prev_high - prev_low))

                if price_touched_zone and bearish_confirm:
                    entry  = self.data.Close[i]
                    sl_dist = max(prev_high - entry + atr * 0.25, atr * 0.5)
                    tp_dist = sl_dist * self.risk_reward
                    sl = entry + sl_dist
                    tp = entry - tp_dist

                    size = self._calc_size(entry, sl)
                    if size > 0:
                        self.sell(sl=sl, tp=tp, size=size)
                        self._last_trade_day  = current_day
                        self._waiting_retest  = False
                        self._breakout_dir    = 0

    def _calc_size(self, entry: float, sl: float) -> float:
        """Calculate position size so risk = max_risk_pct % of current equity."""
        equity   = self.equity
        risk_amt = equity * self.max_risk_pct / 100.0
        sl_dist  = abs(entry - sl)
        if sl_dist <= 0:
            return 0
        # For backtesting.py, size is in units (1 unit = 1 oz for XAUUSD)
        raw_size = risk_amt / sl_dist
        # Minimum 0.01 lot (1 oz in most brokers), step 0.01
        size = max(0.01, round(raw_size, 2))
        return size


# ---------------------------------------------------------------------------
# Load data helper (uses yfinance; replace with broker CSV if available)
# ---------------------------------------------------------------------------
def load_xauusd_data(start: str = "2023-01-01", end: str = "2024-12-31") -> pd.DataFrame:
    """
    Downloads XAUUSD M15 data via yfinance.
    Note: yfinance only provides 60 days of 15m data at a time.
    For longer history, export data from MT5/TradingView and load a CSV.
    """
    try:
        import yfinance as yf
        ticker = yf.Ticker("GC=F")     # Gold futures (proxy for XAUUSD)
        df = ticker.history(period="60d", interval="15m")
        df.index = df.index.tz_localize(None) if df.index.tzinfo is not None else df.index
        df = df[["Open", "High", "Low", "Close", "Volume"]]
        df.dropna(inplace=True)
        return df
    except Exception as e:
        print(f"yfinance error: {e}")
        return None


def load_csv_data(filepath: str) -> pd.DataFrame:
    """
    Load OHLCV data from a CSV file.
    Expected columns: datetime, open, high, low, close, volume
    """
    df = pd.read_csv(filepath, parse_dates=["datetime"], index_col="datetime")
    df.columns = [c.capitalize() for c in df.columns]
    df.index.name = None
    df.dropna(inplace=True)
    return df


# ---------------------------------------------------------------------------
# Run backtest
# ---------------------------------------------------------------------------
def run_backtest(data: pd.DataFrame, cash: float = 10_000, optimize: bool = False):
    bt = Backtest(
        data,
        XAUUSDBreakoutRetest,
        cash=cash,
        commission=0.0002,      # ~0.02% per trade (spread approx for XAUUSD)
        exclusive_orders=True,
        trade_on_close=True,
    )

    if optimize:
        print("\nRunning parameter optimisation (this may take a few minutes)...")
        stats = bt.optimize(
            swing_lookback      = range(15, 30, 5),
            max_retest_candles  = range(4, 10, 2),
            risk_reward         = [2.0, 2.5, 3.0],
            min_breakout_pts    = [1.0, 1.5, 2.0],
            maximize            = "Return [%]",
            constraint          = lambda p: p.risk_reward >= 2.0,
            return_heatmap      = False,
        )
    else:
        stats = bt.run()

    return bt, stats


def print_monthly_summary(stats):
    """Print month-by-month performance."""
    trades = stats._trades
    if trades is None or len(trades) == 0:
        print("No trades found.")
        return

    trades = trades.copy()
    trades["Month"] = pd.to_datetime(trades["EntryTime"]).dt.to_period("M")
    monthly = trades.groupby("Month").agg(
        Trades       = ("PnL", "count"),
        Win_Rate_Pct = ("PnL", lambda x: (x > 0).mean() * 100),
        Total_PnL    = ("PnL", "sum"),
        Max_Loss     = ("PnL", "min"),
        Best_Trade   = ("PnL", "max"),
    ).round(2)
    print("\n=== Monthly Performance ===")
    print(monthly.to_string())
    return monthly


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
if __name__ == "__main__":
    print("XAUUSD M15 Breakout & Retest Backtester")
    print("=" * 50)

    # --- Load data ---
    print("\nLoading data via yfinance (last 60 days, 15m)...")
    data = load_xauusd_data()

    if data is None or len(data) < 100:
        print("\nCould not load live data.")
        print("To backtest with full history, export M15 XAUUSD data from MT5:")
        print("  MT5 → Tools → History Center → Export → CSV")
        print("Then call: data = load_csv_data('your_file.csv')")
    else:
        print(f"Loaded {len(data)} bars from {data.index[0]} to {data.index[-1]}")

        # --- Run backtest ---
        bt, stats = run_backtest(data, cash=10_000)

        print("\n=== Backtest Results ===")
        print(f"Start Balance    : $10,000")
        print(f"End Balance      : ${stats['Equity Final [$]']:,.2f}")
        print(f"Total Return     : {stats['Return [%]']:.2f}%")
        print(f"Max Drawdown     : {stats['Max. Drawdown [%]']:.2f}%")
        print(f"Sharpe Ratio     : {stats['Sharpe Ratio']:.2f}")
        print(f"Win Rate         : {stats['Win Rate [%]']:.1f}%")
        print(f"Total Trades     : {stats['# Trades']}")
        print(f"Profit Factor    : {stats.get('Profit Factor', 'N/A')}")
        print(f"Avg Trade        : ${stats['Avg. Trade [%]']:.2f}%")
        print(f"Best Trade       : {stats['Best Trade [%]']:.2f}%")
        print(f"Worst Trade      : {stats['Worst Trade [%]']:.2f}%")

        monthly = print_monthly_summary(stats)

        print("\n=== Risk Parameters ===")
        print(f"Max risk/trade   : {MAX_RISK_PCT}% of balance")
        print(f"Risk:Reward      : 1:{RISK_REWARD}")
        print(f"Required win rate for breakeven: {1/(1+RISK_REWARD)*100:.1f}%")
        print(f"Monthly target   : 5%")
        print(f"Session          : {SESSION_START}:00 - {SESSION_END}:00 UTC")

        # Show chart
        bt.plot(filename="xauusd_backtest.html", open_browser=False)
        print("\nChart saved to: xauusd_backtest.html")
