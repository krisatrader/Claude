// ============================================================================
//  XAUUSD Breakout+Retest – AKTÍV verzió (BE 1.5×SL + Trail | 2 párhuzamos)
//  Timeframe : M15  |  Instrument : XAUUSD  |  Direction : Long Only
//  Platform  : cTrader / cAlgo (.NET)
// ============================================================================
//
//  Backtest 2025 ($10,000 számla, 1% kockázat/kötés, $0.60/oz FTMO jutalék):
//    110 kötés  |  10.0/hó  |  56.4% WR  |  +83.57% total  |  +7.60%/hó avg
//    Max DD: 4.74%  (FTMO 9% limit alatt)
//    Havi: Jan+8.46% Feb+7.63% Mar+11.54% Apr+6.70% May+0.93%
//          Jun-4.09% Jul+0.67% Aug+4.90% Sep+20.41% Oct+26.55% Nov-0.14%
//
//  Különbségek a konzervatív (BE 2.0×SL, max 1 trade/nap) verzióhoz képest:
//    • HTF filter: 5-réteg → 3-réteg (D1 EMA slope elhagyva)
//      W1 EMA10 + W1 momentum + H4 EMA20 + H4 ADX≥20
//    • Swing szintek: [8,15,25] → [5,8,12,15,20,25] (6 gép párhuzamosan)
//    • Egyidejű pozíciók: 1 → 2 (2 slot egymástól függetlenül)
//    • R:R: 4.0 → 3.0  (SL=0.8%, TP=2.4%)
//    • BE trigger: 2.0×SL → 1.5×SL
//    • Max kötés/nap: 1-2 → 4
//    • Eredmény: 3.7/hó → 10.0/hó, +4.12%/hó → +7.60%/hó
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class XAUUSD_BreakoutRetest_Active : Robot
    {
        // ── Entry ────────────────────────────────────────────────────────────
        [Parameter("Swing Lookback 1", Group = "Entry", DefaultValue = 5,  MinValue = 3)]
        public int SL1 { get; set; }
        [Parameter("Swing Lookback 2", Group = "Entry", DefaultValue = 8,  MinValue = 3)]
        public int SL2 { get; set; }
        [Parameter("Swing Lookback 3", Group = "Entry", DefaultValue = 12, MinValue = 3)]
        public int SL3 { get; set; }
        [Parameter("Swing Lookback 4", Group = "Entry", DefaultValue = 15, MinValue = 3)]
        public int SL4 { get; set; }
        [Parameter("Swing Lookback 5", Group = "Entry", DefaultValue = 20, MinValue = 3)]
        public int SL5 { get; set; }
        [Parameter("Swing Lookback 6", Group = "Entry", DefaultValue = 25, MinValue = 3)]
        public int SL6 { get; set; }
        [Parameter("Max Retest Candles", Group = "Entry", DefaultValue = 10, MinValue = 1)]
        public int MaxRetestCandles { get; set; }
        [Parameter("Retest Zone %",      Group = "Entry", DefaultValue = 0.30)]
        public double RetestZonePct { get; set; }
        [Parameter("Min Breakout ($)",   Group = "Entry", DefaultValue = 0.60)]
        public double MinBreakoutPts { get; set; }

        // ── Risk / Reward ────────────────────────────────────────────────────
        [Parameter("SL %",              Group = "Risk/Reward", DefaultValue = 0.8)]
        public double SlPct { get; set; }
        [Parameter("TP %",              Group = "Risk/Reward", DefaultValue = 2.4)]
        public double TpPct { get; set; }
        [Parameter("Risk Per Trade %",  Group = "Risk/Reward", DefaultValue = 1.0)]
        public double RiskPct { get; set; }
        [Parameter("Max Position (oz)", Group = "Risk/Reward", DefaultValue = 35.0)]
        public double MaxOz { get; set; }
        [Parameter("Max Concurrent Pos",Group = "Risk/Reward", DefaultValue = 2, MinValue = 1, MaxValue = 5)]
        public int MaxConcurrent { get; set; }

        // ── Breakeven / Trail ────────────────────────────────────────────────
        [Parameter("BE Trigger (× SL)", Group = "BE/Trail", DefaultValue = 1.5)]
        public double BeTriggerXSL { get; set; }
        [Parameter("Trail Stop %",      Group = "BE/Trail", DefaultValue = 0.8)]
        public double TrailPct { get; set; }

        // ── HTF Filter (3 réteg – D1 slope nélkül) ───────────────────────────
        [Parameter("W1 EMA Period",     Group = "HTF", DefaultValue = 10)]
        public int W1EmaPeriod { get; set; }
        [Parameter("H4 EMA Period",     Group = "HTF", DefaultValue = 20)]
        public int H4EmaPeriod { get; set; }
        [Parameter("H4 ADX Period",     Group = "HTF", DefaultValue = 14)]
        public int H4AdxPeriod { get; set; }
        [Parameter("H4 ADX Threshold",  Group = "HTF", DefaultValue = 20.0)]
        public double AdxThreshold { get; set; }

        // ── FTMO ─────────────────────────────────────────────────────────────
        [Parameter("Max Trades/Day",    Group = "FTMO", DefaultValue = 4,   MinValue = 1)]
        public int MaxTradesPerDay { get; set; }
        [Parameter("Daily Loss Limit %",Group = "FTMO", DefaultValue = 4.5)]
        public double DailyLossLimitPct { get; set; }
        [Parameter("Max Drawdown %",    Group = "FTMO", DefaultValue = 9.0)]
        public double MaxDdPct { get; set; }
        [Parameter("Session Start UTC", Group = "FTMO", DefaultValue = 7)]
        public int SessionStart { get; set; }
        [Parameter("Session End UTC",   Group = "FTMO", DefaultValue = 18)]
        public int SessionEnd { get; set; }

        // ────────────────────────────────────────────────────────────────────
        //  Private state
        // ────────────────────────────────────────────────────────────────────

        private Bars _w1, _h4;
        private ExponentialMovingAverage _w1Ema, _h4Ema;
        private DirectionalMovementSystem _h4Adx;

        private class SwingState
        {
            public bool   WaitingRetest;
            public double BreakoutLevel;
            public int    BarsSince;
        }

        private int[]        _swingLevels;
        private SwingState[] _states;

        // BE tracking per position
        private Dictionary<long, bool> _beTriggered = new Dictionary<long, bool>();

        // FTMO
        private double   _peakBalance;
        private double   _dayStartBalance;
        private int      _tradesToday;
        private DateTime _currentDay;

        private const string LABEL = "BRACT";

        // ────────────────────────────────────────────────────────────────────
        //  Lifecycle
        // ────────────────────────────────────────────────────────────────────

        protected override void OnStart()
        {
            _w1 = MarketData.GetBars(TimeFrame.Weekly);
            _h4 = MarketData.GetBars(TimeFrame.Hour4);

            _w1Ema = Indicators.ExponentialMovingAverage(_w1.ClosePrices, W1EmaPeriod);
            _h4Ema = Indicators.ExponentialMovingAverage(_h4.ClosePrices, H4EmaPeriod);
            _h4Adx = Indicators.DirectionalMovementSystem(_h4, H4AdxPeriod);

            _swingLevels = new[] { SL1, SL2, SL3, SL4, SL5, SL6 };
            _states = new SwingState[6];
            for (int i = 0; i < 6; i++) _states[i] = new SwingState();

            _peakBalance     = Account.Balance;
            _dayStartBalance = Account.Balance;
            _tradesToday     = 0;
            _currentDay      = Server.Time.Date;

            Print($"[START] XAUUSD-Active | SL={SlPct}% TP={TpPct}% RR={TpPct/SlPct:F1}:1 " +
                  $"BE@{BeTriggerXSL}×SL Trail={TrailPct}% MaxPos={MaxConcurrent}");
        }

        // ────────────────────────────────────────────────────────────────────
        //  OnBar
        // ────────────────────────────────────────────────────────────────────

        protected override void OnBar()
        {
            var today = Server.Time.Date;
            if (today > _currentDay)
            {
                _currentDay      = today;
                _tradesToday     = 0;
                _dayStartBalance = Account.Balance;
            }
            if (Account.Balance > _peakBalance) _peakBalance = Account.Balance;

            if (!PassesFtmo())  return;
            if (!IsInSession()) return;
            if (!IsHtfBull())   return;

            // Kötés ha van szabad slot
            int openCount = Positions.FindAll(LABEL, SymbolName).Length;
            if (openCount >= MaxConcurrent) return;

            TryEntry(MaxConcurrent - openCount);
        }

        // ────────────────────────────────────────────────────────────────────
        //  OnTick – BE + trailing
        // ────────────────────────────────────────────────────────────────────

        protected override void OnTick()
        {
            if (Account.Balance > _peakBalance) _peakBalance = Account.Balance;
            foreach (var pos in Positions.FindAll(LABEL, SymbolName))
                ManagePosition(pos);
        }

        protected override void OnPositionClosed(PositionClosedEventArgs args)
        {
            var pos = args.Position;
            if (pos.Label != LABEL) return;
            _beTriggered.Remove(pos.Id);
            Print($"[CLOSE] {args.Reason,8} | NetPnL={pos.NetProfit:F2}");
        }

        // ════════════════════════════════════════════════════════════════════
        //  SWING STATE MACHINES
        // ════════════════════════════════════════════════════════════════════

        private void TryEntry(int freeSlots)
        {
            int entered = 0;
            for (int i = 0; i < _states.Length && entered < freeSlots; i++)
            {
                if (TryEntryForLevel(i)) entered++;
            }
        }

        private bool TryEntryForLevel(int idx)
        {
            int        n  = _swingLevels[idx];
            SwingState st = _states[idx];

            if (Bars.Count < n + 3) return false;

            double barClose = Bars.ClosePrices.Last(1);
            double barOpen  = Bars.OpenPrices.Last(1);
            double barLow   = Bars.LowPrices.Last(1);
            bool   bullish  = barClose > barOpen;

            if (!st.WaitingRetest)
            {
                double sh = SwingHigh(n);
                if (sh <= 0) return false;

                if (barClose > sh + MinBreakoutPts)
                {
                    double zTop = sh * (1.0 + RetestZonePct / 100.0);
                    if (barLow <= zTop && bullish)
                    {
                        EnterLong();
                        st.WaitingRetest = false;
                        return true;
                    }
                    st.WaitingRetest = true;
                    st.BreakoutLevel = sh;
                    st.BarsSince     = 0;
                }
            }
            else
            {
                st.BarsSince++;
                if (st.BarsSince > MaxRetestCandles) { st.WaitingRetest = false; return false; }

                double lvl  = st.BreakoutLevel;
                double zTop = lvl * (1.0 + RetestZonePct / 100.0);
                double zBot = lvl * (1.0 - RetestZonePct / 100.0);

                if (barClose < zBot) { st.WaitingRetest = false; return false; }

                if (barLow <= zTop && bullish)
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
            double slPx   = Round(ask - slDist);
            double tpPx   = Round(ask + ask * (TpPct / 100.0));
            double vol    = CalcVol(ask, slDist);

            if (vol < Symbol.VolumeInUnitsMin) return;

            var r = ExecuteMarketOrder(TradeType.Buy, SymbolName, vol, LABEL, slPx, tpPx);
            if (r.IsSuccessful)
            {
                _tradesToday++;
                _beTriggered[r.Position.Id] = false;
                Print($"[ENTRY] Ask={ask:F2} SL={slPx:F2} TP={tpPx:F2} Vol={vol:F1}oz #{_tradesToday}/{MaxTradesPerDay}");
            }
            else
                Print($"[ENTRY FAIL] {r.Error}");
        }

        // ════════════════════════════════════════════════════════════════════
        //  POZÍCIÓKEZELÉS
        // ════════════════════════════════════════════════════════════════════

        private void ManagePosition(Position pos)
        {
            if (pos.TradeType != TradeType.Buy) return;
            if (!_beTriggered.ContainsKey(pos.Id)) _beTriggered[pos.Id] = false;

            double bid    = Symbol.Bid;
            double entry  = pos.EntryPrice;
            double slDist = entry * (SlPct / 100.0);

            if (!_beTriggered[pos.Id])
            {
                if (bid >= entry + slDist * BeTriggerXSL)
                {
                    double newSL = Round(entry + Symbol.PipSize);
                    if (pos.StopLoss == null || newSL > pos.StopLoss.Value + Symbol.PipSize)
                    {
                        var r = ModifyPosition(pos, newSL, pos.TakeProfit);
                        if (r.IsSuccessful)
                        {
                            _beTriggered[pos.Id] = true;
                            Print($"[BE] {bid:F2} → SL={newSL:F2}");
                        }
                    }
                }
            }
            else
            {
                double newSL = Round(bid - bid * (TrailPct / 100.0));
                if (pos.StopLoss == null || newSL > pos.StopLoss.Value + Symbol.PipSize)
                    ModifyPosition(pos, newSL, pos.TakeProfit);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  HTF BIAS – 3 réteg (W1 EMA10 + W1 momentum + H4 EMA20 + H4 ADX)
        // ════════════════════════════════════════════════════════════════════

        private bool IsHtfBull()
        {
            if (_w1.Count < W1EmaPeriod + 5)               return false;
            if (_h4.Count < H4EmaPeriod + H4AdxPeriod + 5) return false;

            // L1: W1 ár > W1 EMA10 (shift-1 a look-ahead megelőzéshez)
            if (_w1.ClosePrices.Last(1) <= _w1Ema.Result.Last(2)) return false;

            // L2: W1 momentum (ez a hét záró > előző hét záró)
            if (_w1.ClosePrices.Last(1) <= _w1.ClosePrices.Last(2)) return false;

            // L3: H4 ár > H4 EMA20
            if (_h4.ClosePrices.Last(1) <= _h4Ema.Result.Last(1)) return false;

            // L4: H4 ADX ≥ küszöb (trendelő piac)
            if (_h4Adx.ADX.Last(1) < AdxThreshold) return false;

            return true;
        }

        // ════════════════════════════════════════════════════════════════════
        //  FTMO + SESSION
        // ════════════════════════════════════════════════════════════════════

        private bool PassesFtmo()
        {
            if (_tradesToday >= MaxTradesPerDay) return false;

            double dayLoss = -((Account.Balance - _dayStartBalance) / _dayStartBalance) * 100.0;
            if (dayLoss >= DailyLossLimitPct)
            {
                Print($"[FTMO] Napi limit: {dayLoss:F2}%");
                return false;
            }

            double dd = (_peakBalance - Account.Balance) / _peakBalance * 100.0;
            if (dd >= MaxDdPct)
            {
                Print($"[FTMO] Max DD: {dd:F2}% — leállítva!");
                return false;
            }
            return true;
        }

        private bool IsInSession()
        {
            var t = Server.Time;
            if (t.DayOfWeek == DayOfWeek.Saturday || t.DayOfWeek == DayOfWeek.Sunday) return false;
            return t.Hour >= SessionStart && t.Hour < SessionEnd;
        }

        // ════════════════════════════════════════════════════════════════════
        //  HELPERS
        // ════════════════════════════════════════════════════════════════════

        // Max High a breakout bar (Last(1)) ELŐTTI n barban: Last(2)…Last(n+1)
        private double SwingHigh(int n)
        {
            if (Bars.Count < n + 2) return 0;
            double max = 0;
            for (int i = 2; i <= n + 1; i++)
                max = Math.Max(max, Bars.HighPrices.Last(i));
            return max;
        }

        private double CalcVol(double entry, double slDist)
        {
            double risk  = Account.Equity * (RiskPct / 100.0);
            double uppp  = Symbol.TickValue / Symbol.TickSize;
            if (uppp <= 0) return 0;
            double units = Math.Min(risk / (slDist * uppp), MaxOz);
            return Symbol.NormalizeVolumeInUnits(units, RoundingMode.Down);
        }

        private double Round(double price)
        {
            double t = Symbol.TickSize;
            return Math.Round(price / t) * t;
        }
    }
}
