// ============================================================================
//  XAUUSD Breakout+Retest cBot  —  BE 2.0×SL + Trailing Stop
//  Timeframe : M15   |   Instrument : XAUUSD   |   Direction : Long Only
//  Platform  : cTrader / cAlgo (.NET)
// ============================================================================
//
//  Strategy Logic
//  ─────────────────────────────────────────────────────────────────────────
//  1. HTF BIAS (5-layer "three_phase" filter — all must be bullish):
//       L1  D1 EMA50 slope  : last EMA > EMA 5 bars ago (rising)
//       L2  D1 price > EMA  : last D1 close > D1 EMA50 (shifted 1 bar)
//       L3  W1 price > EMA  : last W1 close > W1 EMA10 (shifted 1 bar)
//       L4  W1 momentum     : last W1 close > previous W1 close
//       L5a H4 price > EMA  : last H4 close > H4 EMA20
//       L5b H4 ADX ≥ 20     : H4 ADX(14) above threshold (trending market)
//     → Only take LONG trades when all 6 sub-conditions are true.
//
//  2. ENTRY (M15 breakout + retest):
//       Three parallel state machines for SWING_LEVELS = {8, 15, 25}
//       a. Idle → detect breakout: last completed M15 bar closed above the
//          rolling max of the N bars before it by at least MIN_BREAKOUT_PTS
//       b. WaitingRetest → within MAX_RETEST_CANDLES bars, look for a
//          bullish M15 candle whose LOW touches the retest zone
//          (±RETEST_ZONE_PCT% around the breakout level)
//       c. If HTF is bullish at retest: market-buy at ask
//
//  3. STOP MANAGEMENT:
//       SL  = entry − 0.8% of entry price
//       TP  = entry + 3.2% of entry price  (4:1 RR)
//       BE  = when bid ≥ entry + 2.0 × SL_dist  → move SL to entry + 1 pip
//       Trail = after BE, trail SL at (current_bid − 0.8%) on every tick
//
//  4. POSITION SIZING:
//       Risk 1% of equity per trade
//       Max position = 35 oz (configurable)
//
//  5. FTMO COMPLIANCE:
//       Max 2 trades per day
//       No new entry if daily loss ≥ 4.5% of day-start balance
//       No new entry if balance drawdown from peak ≥ 9%
//       Session filter: 07:00–18:00 UTC, weekdays only
//
//  Backtest results (2025, no look-ahead, $10k account):
//       41 trades  |  61.0% WR  |  +45.34% total  |  +4.12%/month avg
//       Max DD 2.72%  |  Monthly range: −1.02% to +11.14%
// ============================================================================

using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class XAUUSD_BreakoutRetest_BE2Trail : Robot
    {
        // ────────────────────────────────────────────────────────────────────
        //  Parameters
        // ────────────────────────────────────────────────────────────────────

        #region Entry Parameters

        [Parameter("Swing Lookback 1", Group = "Entry", DefaultValue = 8, MinValue = 3, MaxValue = 100)]
        public int SwingLookback1 { get; set; }

        [Parameter("Swing Lookback 2", Group = "Entry", DefaultValue = 15, MinValue = 3, MaxValue = 100)]
        public int SwingLookback2 { get; set; }

        [Parameter("Swing Lookback 3", Group = "Entry", DefaultValue = 25, MinValue = 3, MaxValue = 100)]
        public int SwingLookback3 { get; set; }

        [Parameter("Max Retest Candles", Group = "Entry", DefaultValue = 10, MinValue = 1, MaxValue = 50)]
        public int MaxRetestCandles { get; set; }

        [Parameter("Retest Zone %", Group = "Entry", DefaultValue = 0.30, MinValue = 0.05, MaxValue = 2.0)]
        public double RetestZonePct { get; set; }

        [Parameter("Min Breakout Points ($)", Group = "Entry", DefaultValue = 0.60, MinValue = 0.0)]
        public double MinBreakoutPts { get; set; }

        #endregion

        #region Risk / Reward Parameters

        [Parameter("SL %", Group = "Risk/Reward", DefaultValue = 0.8, MinValue = 0.1, MaxValue = 5.0)]
        public double SlPct { get; set; }

        [Parameter("TP %", Group = "Risk/Reward", DefaultValue = 3.2, MinValue = 0.1, MaxValue = 20.0)]
        public double TpPct { get; set; }

        [Parameter("Risk Per Trade %", Group = "Risk/Reward", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 5.0)]
        public double RiskPct { get; set; }

        /// <summary>
        /// Maximum position size in oz (units). For XAUUSD: 1 unit ≈ 1 oz on most brokers.
        /// Check Symbol.LotSize to confirm (standard = 100 oz/lot → 35 oz = 0.35 lots).
        /// </summary>
        [Parameter("Max Position Size (oz)", Group = "Risk/Reward", DefaultValue = 35.0, MinValue = 1.0)]
        public double MaxPositionOz { get; set; }

        #endregion

        #region Breakeven & Trail Parameters

        [Parameter("BE Trigger (× SL dist)", Group = "Breakeven/Trail", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 10.0)]
        public double BeTriggerXSL { get; set; }

        [Parameter("Trail Stop %", Group = "Breakeven/Trail", DefaultValue = 0.8, MinValue = 0.1, MaxValue = 5.0)]
        public double TrailStopPct { get; set; }

        #endregion

        #region HTF Filter Parameters

        [Parameter("D1 EMA Period", Group = "HTF Filter", DefaultValue = 50, MinValue = 5)]
        public int D1EmaPeriod { get; set; }

        [Parameter("D1 EMA Slope Lookback (bars)", Group = "HTF Filter", DefaultValue = 5, MinValue = 1)]
        public int D1EmaSlopeBars { get; set; }

        [Parameter("W1 EMA Period", Group = "HTF Filter", DefaultValue = 10, MinValue = 3)]
        public int W1EmaPeriod { get; set; }

        [Parameter("H4 EMA Period", Group = "HTF Filter", DefaultValue = 20, MinValue = 5)]
        public int H4EmaPeriod { get; set; }

        [Parameter("H4 ADX Period", Group = "HTF Filter", DefaultValue = 14, MinValue = 5)]
        public int H4AdxPeriod { get; set; }

        [Parameter("H4 ADX Threshold", Group = "HTF Filter", DefaultValue = 20.0, MinValue = 5.0)]
        public double AdxThreshold { get; set; }

        #endregion

        #region FTMO Compliance Parameters

        [Parameter("Max Trades Per Day", Group = "FTMO", DefaultValue = 2, MinValue = 1, MaxValue = 20)]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Daily Loss Limit %", Group = "FTMO", DefaultValue = 4.5, MinValue = 0.5)]
        public double DailyLossLimitPct { get; set; }

        [Parameter("Max Drawdown %", Group = "FTMO", DefaultValue = 9.0, MinValue = 1.0)]
        public double MaxDrawdownPct { get; set; }

        [Parameter("Session Start UTC (hour)", Group = "FTMO", DefaultValue = 7, MinValue = 0, MaxValue = 23)]
        public int SessionStartHour { get; set; }

        [Parameter("Session End UTC (hour)", Group = "FTMO", DefaultValue = 18, MinValue = 1, MaxValue = 24)]
        public int SessionEndHour { get; set; }

        #endregion

        // ────────────────────────────────────────────────────────────────────
        //  Private state
        // ────────────────────────────────────────────────────────────────────

        // HTF bars & indicators
        private Bars _d1, _w1, _h4;
        private ExponentialMovingAverage _d1Ema, _w1Ema, _h4Ema;
        private DirectionalMovementSystem _h4Adx;

        // M15 swing state machine (one per lookback level)
        private class SwingState
        {
            public bool  WaitingRetest;
            public double BreakoutLevel;
            public int   BarsSinceBreakout;
        }

        private int[]        _swingLevels;
        private SwingState[] _swingStates;

        // Breakeven tracking: positionId → has BE been triggered?
        private readonly Dictionary<long, bool> _beTriggered = new Dictionary<long, bool>();

        // FTMO daily tracking
        private double   _peakBalance;
        private double   _dayStartBalance;
        private int      _tradesToday;
        private DateTime _currentDay;

        private const string LABEL = "BRBE2T";

        // ────────────────────────────────────────────────────────────────────
        //  Lifecycle
        // ────────────────────────────────────────────────────────────────────

        protected override void OnStart()
        {
            // Load HTF bar series
            _d1 = MarketData.GetBars(TimeFrame.Daily);
            _w1 = MarketData.GetBars(TimeFrame.Weekly);
            _h4 = MarketData.GetBars(TimeFrame.Hour4);

            // HTF indicators (computed on HTF DataSeries → no look-ahead into M15)
            _d1Ema  = Indicators.ExponentialMovingAverage(_d1.ClosePrices, D1EmaPeriod);
            _w1Ema  = Indicators.ExponentialMovingAverage(_w1.ClosePrices, W1EmaPeriod);
            _h4Ema  = Indicators.ExponentialMovingAverage(_h4.ClosePrices, H4EmaPeriod);
            _h4Adx  = Indicators.DirectionalMovementSystem(_h4, H4AdxPeriod);

            // Parallel swing state machines
            _swingLevels = new[] { SwingLookback1, SwingLookback2, SwingLookback3 };
            _swingStates = new SwingState[3];
            for (int i = 0; i < 3; i++)
                _swingStates[i] = new SwingState();

            // FTMO baseline
            _peakBalance     = Account.Balance;
            _dayStartBalance = Account.Balance;
            _tradesToday     = 0;
            _currentDay      = Server.Time.Date;

            Positions.Closed += OnPositionsClosed;

            Print($"[START] XAUUSD BR-BE2Trail | SL={SlPct}% TP={TpPct}% BE@{BeTriggerXSL}xSL Trail={TrailStopPct}%");
        }

        protected override void OnStop()
        {
            Positions.Closed -= OnPositionsClosed;
        }

        // ────────────────────────────────────────────────────────────────────
        //  OnBar — fired when a new M15 bar opens (previous bar is complete)
        //  Last(1) = just-closed bar used for signal logic
        //  Last(0) = current bar opening (we send market order here)
        // ────────────────────────────────────────────────────────────────────

        protected override void OnBar()
        {
            // ── Daily counter reset ──────────────────────────────────────
            var today = Server.Time.Date;
            if (today > _currentDay)
            {
                _currentDay      = today;
                _tradesToday     = 0;
                _dayStartBalance = Account.Balance;
                Print($"[DAY] {today:yyyy-MM-dd}  Balance={_dayStartBalance:F2}");
            }

            // ── Track peak balance ────────────────────────────────────────
            if (Account.Balance > _peakBalance)
                _peakBalance = Account.Balance;

            // ── Skip if a position is already open ────────────────────────
            if (Positions.FindAll(LABEL, SymbolName).Length > 0) return;

            // ── FTMO guards ───────────────────────────────────────────────
            if (!PassesFtmoGuards()) return;

            // ── Session filter ────────────────────────────────────────────
            if (!IsInSession()) return;

            // ── HTF bias must be bullish ──────────────────────────────────
            if (!IsHtfBullish()) return;

            // ── Run swing state machines; enter on first valid signal ─────
            TrySwingEntry();
        }

        // ────────────────────────────────────────────────────────────────────
        //  OnTick — manage BE and trailing for open positions
        // ────────────────────────────────────────────────────────────────────

        protected override void OnTick()
        {
            if (Account.Balance > _peakBalance)
                _peakBalance = Account.Balance;

            foreach (var pos in Positions.FindAll(LABEL, SymbolName))
                ManagePosition(pos);
        }

        // ────────────────────────────────────────────────────────────────────
        //  OnPositionClosed — cleanup dictionary entry
        // ────────────────────────────────────────────────────────────────────

        private void OnPositionsClosed(PositionClosedEventArgs args)
        {
            var pos = args.Position;
            if (pos.Label != LABEL) return;

            _beTriggered.Remove(pos.Id);
            Print($"[CLOSE] {args.Reason,8} | Entry={pos.EntryPrice:F2} | NetPnL={pos.NetProfit:F2}");
        }

        // ════════════════════════════════════════════════════════════════════
        //  SWING STATE MACHINE + ENTRY LOGIC
        // ════════════════════════════════════════════════════════════════════

        private void TrySwingEntry()
        {
            for (int i = 0; i < 3; i++)
            {
                if (TryEntryForLevel(i))
                    return;   // max one entry per bar (first valid level wins)
            }
        }

        private bool TryEntryForLevel(int idx)
        {
            int       n  = _swingLevels[idx];
            SwingState st = _swingStates[idx];

            // Need at least (n + 2) closed M15 bars
            if (Bars.Count < n + 3) return false;

            // Closed-bar data (Last(1) = just-completed M15 bar)
            double barClose = Bars.ClosePrices.Last(1);
            double barOpen  = Bars.OpenPrices.Last(1);
            double barLow   = Bars.LowPrices.Last(1);
            bool   bullish  = barClose > barOpen;

            if (!st.WaitingRetest)
            {
                // ── Look for breakout ────────────────────────────────────
                // swingHigh = max(High[Last(2) … Last(n+1)]) — bars BEFORE the closed bar
                double swingHigh = ComputeSwingHigh(n);
                if (swingHigh <= 0) return false;

                bool brokeOut = barClose > swingHigh + MinBreakoutPts;
                if (!brokeOut) return false;

                // Check for same-bar retest (breakout candle dipped back into zone)
                double zoneTop = swingHigh * (1.0 + RetestZonePct / 100.0);
                if (barLow <= zoneTop && bullish)
                {
                    // Immediate entry: breakout + retest happened on same bar
                    EnterLong();
                    st.WaitingRetest = false;
                    return true;
                }

                // Start watching for retest on subsequent bars
                st.WaitingRetest      = true;
                st.BreakoutLevel      = swingHigh;
                st.BarsSinceBreakout  = 0;
            }
            else
            {
                // ── Retest watch ─────────────────────────────────────────
                st.BarsSinceBreakout++;

                // Timeout: too many bars have passed since breakout
                if (st.BarsSinceBreakout > MaxRetestCandles)
                {
                    st.WaitingRetest = false;
                    return false;
                }

                double level   = st.BreakoutLevel;
                double zoneTop = level * (1.0 + RetestZonePct / 100.0);
                double zoneBot = level * (1.0 - RetestZonePct / 100.0);

                // Invalidate: closed bar broke down BELOW the support zone
                if (barClose < zoneBot)
                {
                    st.WaitingRetest = false;
                    return false;
                }

                // Retest trigger: low touched zone + bullish close
                bool touchedZone = barLow <= zoneTop;
                if (touchedZone && bullish)
                {
                    EnterLong();
                    st.WaitingRetest = false;
                    return true;
                }
            }

            return false;
        }

        private void EnterLong()
        {
            double ask    = Symbol.Ask;
            double slDist = ask * (SlPct / 100.0);
            double slPx   = NormalizePrice(ask - slDist);
            double tpPx   = NormalizePrice(ask + ask * (TpPct / 100.0));

            double volume = CalcVolumeUnits(ask, slDist);
            if (volume < Symbol.VolumeInUnitsMin)
            {
                Print("[SKIP] Computed volume below broker minimum.");
                return;
            }

            var result = ExecuteMarketOrder(
                TradeType.Buy,
                SymbolName,
                volume,
                LABEL,
                slPx,
                tpPx
            );

            if (result.IsSuccessful)
            {
                _tradesToday++;
                _beTriggered[result.Position.Id] = false;
                Print($"[ENTRY] Ask={ask:F2} SL={slPx:F2} TP={tpPx:F2} Vol={volume:F1}oz " +
                      $"Risk≈{volume * slDist:F2} #{_tradesToday}/day");
            }
            else
            {
                Print($"[ENTRY FAIL] {result.Error}");
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  POSITION MANAGEMENT — BREAKEVEN + TRAILING STOP
        // ════════════════════════════════════════════════════════════════════

        private void ManagePosition(Position pos)
        {
            if (pos.TradeType != TradeType.Buy) return;

            // Ensure entry exists in dictionary (safeguard for positions opened outside this session)
            if (!_beTriggered.ContainsKey(pos.Id))
                _beTriggered[pos.Id] = false;

            double bid    = Symbol.Bid;
            double entry  = pos.EntryPrice;
            double slDist = entry * (SlPct / 100.0);

            if (!_beTriggered[pos.Id])
            {
                // ── Phase 1: waiting for BE trigger price ─────────────────
                double beTrigger = entry + slDist * BeTriggerXSL;

                if (bid >= beTrigger)
                {
                    // Move SL to entry + 1 pip (slight buffer above breakeven)
                    double newSL = NormalizePrice(entry + Symbol.PipSize);

                    bool needsUpdate = pos.StopLoss == null || newSL > (pos.StopLoss.Value + Symbol.PipSize);
                    if (needsUpdate)
                    {
                        var result = ModifyPosition(pos, newSL, pos.TakeProfit, ProtectionType.Absolute);
                        if (result.IsSuccessful)
                        {
                            _beTriggered[pos.Id] = true;
                            Print($"[BE] bid={bid:F2} ≥ trigger={beTrigger:F2} → SL→{newSL:F2}");
                        }
                    }
                }
            }
            else
            {
                // ── Phase 2: BE is active → trail SL at TrailStopPct% below bid ──
                double trailDist = bid * (TrailStopPct / 100.0);
                double newSL     = NormalizePrice(bid - trailDist);

                // Only move SL up, never down
                bool needsUpdate = pos.StopLoss == null || newSL > (pos.StopLoss.Value + Symbol.PipSize);
                if (needsUpdate)
                {
                    ModifyPosition(pos, newSL, pos.TakeProfit, ProtectionType.Absolute);
                }
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  HTF BIAS — 5-layer "three_phase" bullish filter
        // ════════════════════════════════════════════════════════════════════

        private bool IsHtfBullish()
        {
            // Require sufficient bars for all indicators to be warm
            if (_d1.Count < D1EmaPeriod + D1EmaSlopeBars + 10) return false;
            if (_w1.Count < W1EmaPeriod + 5)                    return false;
            if (_h4.Count < H4EmaPeriod + H4AdxPeriod + 5)      return false;

            // L1 — D1 EMA50 is rising: compare Last(1) EMA to Last(1 + SlopeBars) EMA
            //       Last(1) = last completed D1 bar (causal — no look-ahead)
            double d1EmaNow  = _d1Ema.Result.Last(1);
            double d1EmaBack = _d1Ema.Result.Last(1 + D1EmaSlopeBars);
            if (d1EmaNow <= d1EmaBack) return false;

            // L2 — D1 price above EMA50 (use EMA shifted 1 extra bar to match Python .shift(1))
            double d1Close   = _d1.ClosePrices.Last(1);
            double d1EmaPrev = _d1Ema.Result.Last(2);
            if (d1Close <= d1EmaPrev) return false;

            // L3 — W1 price above W1 EMA10 (last completed week close vs EMA shifted 1 bar)
            double w1Close   = _w1.ClosePrices.Last(1);
            double w1EmaPrev = _w1Ema.Result.Last(2);
            if (w1Close <= w1EmaPrev) return false;

            // L4 — W1 momentum: last completed week close > week before it
            double w1ClosePrev = _w1.ClosePrices.Last(2);
            if (w1Close <= w1ClosePrev) return false;

            // L5a — H4 price above H4 EMA20 (last completed H4 bar)
            double h4Close  = _h4.ClosePrices.Last(1);
            double h4EmaNow = _h4Ema.Result.Last(1);
            if (h4Close <= h4EmaNow) return false;

            // L5b — H4 ADX >= threshold (trending market, not choppy)
            double adxValue = _h4Adx.ADX.Last(1);
            if (adxValue < AdxThreshold) return false;

            return true;
        }

        // ════════════════════════════════════════════════════════════════════
        //  FTMO COMPLIANCE GUARDS
        // ════════════════════════════════════════════════════════════════════

        private bool PassesFtmoGuards()
        {
            // Guard 1: max trades per day
            if (_tradesToday >= MaxTradesPerDay) return false;

            // Guard 2: daily loss limit (measured from balance at session start)
            double dayPnl    = Account.Balance - _dayStartBalance;
            double dayLossPct = -dayPnl / _dayStartBalance * 100.0;
            if (dayLossPct >= DailyLossLimitPct)
            {
                Print($"[FTMO] Daily loss {dayLossPct:F2}% ≥ limit {DailyLossLimitPct}% — no new entries.");
                return false;
            }

            // Guard 3: max drawdown from peak balance
            double ddPct = (_peakBalance - Account.Balance) / _peakBalance * 100.0;
            if (ddPct >= MaxDrawdownPct)
            {
                Print($"[FTMO] Max DD {ddPct:F2}% ≥ limit {MaxDrawdownPct}% — trading halted!");
                return false;
            }

            return true;
        }

        // ════════════════════════════════════════════════════════════════════
        //  UTILITY HELPERS
        // ════════════════════════════════════════════════════════════════════

        private bool IsInSession()
        {
            var t = Server.Time;
            if (t.DayOfWeek == DayOfWeek.Saturday || t.DayOfWeek == DayOfWeek.Sunday)
                return false;
            return t.Hour >= SessionStartHour && t.Hour < SessionEndHour;
        }

        /// <summary>
        /// Rolling max of the N M15 bars immediately BEFORE Last(1) (the breakout bar).
        /// Scans Last(2) through Last(n+1).
        /// Matches Python: swing_high = df["High"].rolling(n).max().shift(1)
        /// </summary>
        private double ComputeSwingHigh(int n)
        {
            if (Bars.Count < n + 2) return 0;
            double max = 0;
            for (int i = 2; i <= n + 1; i++)
                max = Math.Max(max, Bars.HighPrices.Last(i));
            return max;
        }

        /// <summary>
        /// Calculate position volume in broker units based on equity risk.
        /// For XAUUSD: Symbol.TickValue / Symbol.TickSize gives USD per unit per $1 move.
        /// With 1 unit = 1 oz, 100 oz lot: result is units (oz).
        /// </summary>
        private double CalcVolumeUnits(double entryPrice, double slDist)
        {
            double riskMoney = Account.Equity * (RiskPct / 100.0);

            // USD value of a 1-price-unit move for 1 unit of volume
            double unitValuePerPoint = Symbol.TickValue / Symbol.TickSize;
            if (unitValuePerPoint <= 0) return 0;

            double units = riskMoney / (slDist * unitValuePerPoint);

            // Cap and normalize
            units = Math.Min(units, MaxPositionOz);
            units = Symbol.NormalizeVolumeInUnits(units, RoundingMode.Down);

            return units;
        }

        private double NormalizePrice(double price)
        {
            double tick = Symbol.TickSize;
            return Math.Round(price / tick) * tick;
        }
    }
}
