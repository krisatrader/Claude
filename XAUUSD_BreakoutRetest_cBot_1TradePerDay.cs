// ============================================================================
//  XAUUSD Breakout+Retest cBot  —  BE 2.0×SL + Trail  |  MAX 1 TRADE/DAY
//  Timeframe : M15   |   Instrument : XAUUSD   |   Direction : Long Only
//  Platform  : cTrader / cAlgo (.NET)
// ============================================================================
//
//  Ez a verzió az eredeti BE 2.0×SL + Trail stratégia konzervatív változata:
//  naponta legfeljebb 1 kötés megengedett.
//
//  Backtest eredmények 2025-ben ($10,000 számla):
//    40 kötés  |  60.0% WR  |  +39.92% total  |  +3.63%/hó
//    Max DD: 1.31%  (FTMO limit: 9%)
//    Exit: 17.5% TP / 42.5% SL-BE (nullkockázat) / 40.0% SL
//
//  Havi bontás (2025):
//    Jan +3.88% | Feb +3.71% | Mar +4.89% | Apr +4.00% | May +0.29%
//    Jun -1.02% | Jul +0.35% | Aug +2.96% | Sep +11.14%| Oct +6.77%| Nov +2.96%
//
//  Különbség a 2-trade/nap verzióhoz képest:
//    • Max DD: 2.72% → 1.31%  (felére csökkent)
//    • Havi hozam: +4.12% → +3.63% (−0.49%/hó kompromisszum)
//    • Kötések: 41 → 40  (minimális különbség 2025-ben)
//
//  Stratégia logika (azonos az alap verzióval):
//    1. 5-rétegű HTF szűrő (D1 EMA50 slope, W1 EMA10, W1 momentum, H4 EMA20, H4 ADX≥20)
//    2. M15 breakout detektálás 3 párhuzamos állapotgéppel (lookback: 8, 15, 25)
//    3. Retest belépés: low érinti a zónát + bullish gyertya
//    4. SL = 0.8%, TP = 3.2% (4:1 R:R), 1% equity kockázat
//    5. BE 2.0×SL távolságnál, utána 0.8% trailing stop
//    6. FTMO: max 1 trade/nap, 4.5% napi limit, 9% max DD, 07-18 UTC session
// ============================================================================

using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class XAUUSD_BreakoutRetest_BE2Trail_1PerDay : Robot
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

        #region FTMO / Risk Control Parameters

        /// <summary>
        /// Naponta maximum 1 kötés (konzervatív mód).
        /// Növeld 2-re, ha az eredeti (2 trade/nap) verziót szeretnéd.
        /// </summary>
        [Parameter("Max Trades Per Day", Group = "FTMO", DefaultValue = 1, MinValue = 1, MaxValue = 20)]
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

        private Bars _d1, _w1, _h4;
        private ExponentialMovingAverage _d1Ema, _w1Ema, _h4Ema;
        private DirectionalMovementSystem _h4Adx;

        private class SwingState
        {
            public bool   WaitingRetest;
            public double BreakoutLevel;
            public int    BarsSinceBreakout;
        }

        private int[]        _swingLevels;
        private SwingState[] _swingStates;

        private readonly Dictionary<long, bool> _beTriggered = new Dictionary<long, bool>();

        private double   _peakBalance;
        private double   _dayStartBalance;
        private int      _tradesToday;
        private DateTime _currentDay;

        private const string LABEL = "BR1T";

        // ────────────────────────────────────────────────────────────────────
        //  Lifecycle
        // ────────────────────────────────────────────────────────────────────

        protected override void OnStart()
        {
            _d1 = MarketData.GetBars(TimeFrame.Daily);
            _w1 = MarketData.GetBars(TimeFrame.Weekly);
            _h4 = MarketData.GetBars(TimeFrame.Hour4);

            _d1Ema = Indicators.ExponentialMovingAverage(_d1.ClosePrices, D1EmaPeriod);
            _w1Ema = Indicators.ExponentialMovingAverage(_w1.ClosePrices, W1EmaPeriod);
            _h4Ema = Indicators.ExponentialMovingAverage(_h4.ClosePrices, H4EmaPeriod);
            _h4Adx = Indicators.DirectionalMovementSystem(_h4, H4AdxPeriod);

            _swingLevels = new[] { SwingLookback1, SwingLookback2, SwingLookback3 };
            _swingStates = new SwingState[3];
            for (int i = 0; i < 3; i++)
                _swingStates[i] = new SwingState();

            _peakBalance     = Account.Balance;
            _dayStartBalance = Account.Balance;
            _tradesToday     = 0;
            _currentDay      = Server.Time.Date;

            Print($"[START] XAUUSD BR-BE2Trail-1PerDay | " +
                  $"SL={SlPct}% TP={TpPct}% BE@{BeTriggerXSL}×SL Trail={TrailStopPct}% MaxTrades={MaxTradesPerDay}/nap");
        }

        // ────────────────────────────────────────────────────────────────────
        //  OnBar — M15 bar zárásakor hívódik
        // ────────────────────────────────────────────────────────────────────

        protected override void OnBar()
        {
            // Napi reset
            var today = Server.Time.Date;
            if (today > _currentDay)
            {
                _currentDay      = today;
                _tradesToday     = 0;
                _dayStartBalance = Account.Balance;
                Print($"[NAP] {today:yyyy-MM-dd}  Balance={_dayStartBalance:F2}");
            }

            if (Account.Balance > _peakBalance)
                _peakBalance = Account.Balance;

            // Csak 1 nyitott pozíció megengedett (single-position model)
            if (Positions.FindAll(LABEL, SymbolName).Length > 0) return;

            if (!PassesFtmoGuards())  return;
            if (!IsInSession())       return;
            if (!IsHtfBullish())      return;

            TrySwingEntry();
        }

        // ────────────────────────────────────────────────────────────────────
        //  OnTick — BE és trailing kezelés
        // ────────────────────────────────────────────────────────────────────

        protected override void OnTick()
        {
            if (Account.Balance > _peakBalance)
                _peakBalance = Account.Balance;

            foreach (var pos in Positions.FindAll(LABEL, SymbolName))
                ManagePosition(pos);
        }

        protected override void OnPositionClosed(PositionClosedEventArgs args)
        {
            var pos = args.Position;
            if (pos.Label != LABEL) return;
            _beTriggered.Remove(pos.Id);
            Print($"[CLOSE] {args.Reason,8} | Entry={pos.EntryPrice:F2} | NetPnL={pos.NetProfit:F2}");
        }

        // ════════════════════════════════════════════════════════════════════
        //  SWING STATE MACHINE + ENTRY
        // ════════════════════════════════════════════════════════════════════

        private void TrySwingEntry()
        {
            for (int i = 0; i < 3; i++)
            {
                if (TryEntryForLevel(i))
                    return;
            }
        }

        private bool TryEntryForLevel(int idx)
        {
            int       n  = _swingLevels[idx];
            SwingState st = _swingStates[idx];

            if (Bars.Count < n + 3) return false;

            double barClose = Bars.ClosePrices.Last(1);
            double barOpen  = Bars.OpenPrices.Last(1);
            double barLow   = Bars.LowPrices.Last(1);
            bool   bullish  = barClose > barOpen;

            if (!st.WaitingRetest)
            {
                double swingHigh = ComputeSwingHigh(n);
                if (swingHigh <= 0) return false;

                if (barClose > swingHigh + MinBreakoutPts)
                {
                    double zoneTop = swingHigh * (1.0 + RetestZonePct / 100.0);
                    if (barLow <= zoneTop && bullish)
                    {
                        EnterLong();
                        st.WaitingRetest = false;
                        return true;
                    }
                    st.WaitingRetest     = true;
                    st.BreakoutLevel     = swingHigh;
                    st.BarsSinceBreakout = 0;
                }
            }
            else
            {
                st.BarsSinceBreakout++;

                if (st.BarsSinceBreakout > MaxRetestCandles)
                {
                    st.WaitingRetest = false;
                    return false;
                }

                double level   = st.BreakoutLevel;
                double zoneTop = level * (1.0 + RetestZonePct / 100.0);
                double zoneBot = level * (1.0 - RetestZonePct / 100.0);

                if (barClose < zoneBot)
                {
                    st.WaitingRetest = false;
                    return false;
                }

                if (barLow <= zoneTop && bullish)
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
                Print("[SKIP] Pozícióméret a minimális határ alatt.");
                return;
            }

            var result = ExecuteMarketOrder(TradeType.Buy, SymbolName, volume, LABEL, slPx, tpPx);

            if (result.IsSuccessful)
            {
                _tradesToday++;
                _beTriggered[result.Position.Id] = false;
                Print($"[ENTRY] Ask={ask:F2} SL={slPx:F2} TP={tpPx:F2} Vol={volume:F1}oz " +
                      $"#{_tradesToday}/{MaxTradesPerDay} kötés ma");
            }
            else
            {
                Print($"[ENTRY FAIL] {result.Error}");
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  POZÍCIÓKEZELÉS — BREAKEVEN + TRAILING STOP
        // ════════════════════════════════════════════════════════════════════

        private void ManagePosition(Position pos)
        {
            if (pos.TradeType != TradeType.Buy) return;
            if (!_beTriggered.ContainsKey(pos.Id))
                _beTriggered[pos.Id] = false;

            double bid    = Symbol.Bid;
            double entry  = pos.EntryPrice;
            double slDist = entry * (SlPct / 100.0);

            if (!_beTriggered[pos.Id])
            {
                // Fázis 1: BE trigger várás
                double beTrigger = entry + slDist * BeTriggerXSL;
                if (bid >= beTrigger)
                {
                    double newSL = NormalizePrice(entry + Symbol.PipSize);
                    bool needsUpdate = pos.StopLoss == null || newSL > (pos.StopLoss.Value + Symbol.PipSize);
                    if (needsUpdate)
                    {
                        var r = ModifyPosition(pos, newSL, pos.TakeProfit);
                        if (r.IsSuccessful)
                        {
                            _beTriggered[pos.Id] = true;
                            Print($"[BE] bid={bid:F2} ≥ trigger={beTrigger:F2} → SL→{newSL:F2}");
                        }
                    }
                }
            }
            else
            {
                // Fázis 2: BE aktív → trailing stop
                double trailDist = bid * (TrailStopPct / 100.0);
                double newSL     = NormalizePrice(bid - trailDist);
                bool needsUpdate = pos.StopLoss == null || newSL > (pos.StopLoss.Value + Symbol.PipSize);
                if (needsUpdate)
                    ModifyPosition(pos, newSL, pos.TakeProfit);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  HTF BIAS — 5-rétegű "three_phase" bullish szűrő
        // ════════════════════════════════════════════════════════════════════

        private bool IsHtfBullish()
        {
            if (_d1.Count < D1EmaPeriod + D1EmaSlopeBars + 10) return false;
            if (_w1.Count < W1EmaPeriod + 5)                    return false;
            if (_h4.Count < H4EmaPeriod + H4AdxPeriod + 5)      return false;

            // R1: D1 EMA50 emelkedő
            if (_d1Ema.Result.Last(1) <= _d1Ema.Result.Last(1 + D1EmaSlopeBars)) return false;

            // R2: D1 ár > D1 EMA50 (shifted)
            if (_d1.ClosePrices.Last(1) <= _d1Ema.Result.Last(2)) return false;

            // R3: W1 ár > W1 EMA10 (shifted)
            if (_w1.ClosePrices.Last(1) <= _w1Ema.Result.Last(2)) return false;

            // R4: W1 momentum (ez a hét > előző hét)
            if (_w1.ClosePrices.Last(1) <= _w1.ClosePrices.Last(2)) return false;

            // R5a: H4 ár > H4 EMA20
            if (_h4.ClosePrices.Last(1) <= _h4Ema.Result.Last(1)) return false;

            // R5b: H4 ADX ≥ küszöb (trending piac)
            if (_h4Adx.ADX.Last(1) < AdxThreshold) return false;

            return true;
        }

        // ════════════════════════════════════════════════════════════════════
        //  FTMO ÉS SESSION ELLENŐRZÉSEK
        // ════════════════════════════════════════════════════════════════════

        private bool PassesFtmoGuards()
        {
            // Max napi kötésszám (ez a fő különbség: alapértelmezetten 1)
            if (_tradesToday >= MaxTradesPerDay) return false;

            // Napi veszteség limit
            double dayPnl     = Account.Balance - _dayStartBalance;
            double dayLossPct = -dayPnl / _dayStartBalance * 100.0;
            if (dayLossPct >= DailyLossLimitPct)
            {
                Print($"[FTMO] Napi veszteség {dayLossPct:F2}% ≥ limit {DailyLossLimitPct}%");
                return false;
            }

            // Max drawdown peak-tól
            double ddPct = (_peakBalance - Account.Balance) / _peakBalance * 100.0;
            if (ddPct >= MaxDrawdownPct)
            {
                Print($"[FTMO] Max DD {ddPct:F2}% ≥ limit {MaxDrawdownPct}% — kereskedés leállítva!");
                return false;
            }

            return true;
        }

        private bool IsInSession()
        {
            var t = Server.Time;
            if (t.DayOfWeek == DayOfWeek.Saturday || t.DayOfWeek == DayOfWeek.Sunday)
                return false;
            return t.Hour >= SessionStartHour && t.Hour < SessionEndHour;
        }

        // ════════════════════════════════════════════════════════════════════
        //  SEGÉDFÜGGVÉNYEK
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// N db M15 bar maximum High-ja, a breakout bar (Last(1)) ELŐTTI barokban.
        /// Egyezik: Python rolling(n).max().shift(1) → Last(2)…Last(n+1)
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
        /// Pozícióméret számítás: equity × kockázat% / (SL_dist × unitValue)
        /// XAUUSD: 1 egység ≈ 1 oz → Symbol.TickValue/TickSize = 1 USD/oz/$
        /// </summary>
        private double CalcVolumeUnits(double entryPrice, double slDist)
        {
            double riskMoney         = Account.Equity * (RiskPct / 100.0);
            double unitValuePerPoint = Symbol.TickValue / Symbol.TickSize;
            if (unitValuePerPoint <= 0) return 0;

            double units = riskMoney / (slDist * unitValuePerPoint);
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
