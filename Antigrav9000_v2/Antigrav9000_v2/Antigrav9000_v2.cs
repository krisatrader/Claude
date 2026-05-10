using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // Mac Backtesthez: AccessRights.FullAccess
    // Cloud Futtatáshoz: AccessRights.None
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class Antigrav9000_v2 : Robot
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
        [Parameter("Initial Account Size (USD)", Group = "Risk/Reward", DefaultValue = 25000.0)]
        public double InitialAccountSize { get; set; }
        [Parameter("SL %",              Group = "Risk/Reward", DefaultValue = 0.8)]
        public double SlPct { get; set; }
        [Parameter("TP %",              Group = "Risk/Reward", DefaultValue = 2.4)]
        public double TpPct { get; set; }
        [Parameter("Risk Per Trade %",  Group = "Risk/Reward", DefaultValue = 1.0)]
        public double RiskPct { get; set; }
        [Parameter("Max Position (Lot)", Group = "Risk/Reward", DefaultValue = 0.35)]
        public double MaxLot { get; set; }
        [Parameter("Max Concurrent Pos",Group = "Risk/Reward", DefaultValue = 2, MinValue = 1, MaxValue = 5)]
        public int MaxConcurrent { get; set; }

        // ── Breakeven / Trail ────────────────────────────────────────────────
        [Parameter("BE Trigger (× SL)", Group = "BE/Trail", DefaultValue = 1.5)]
        public double BeTriggerXSL { get; set; }
        [Parameter("Trail Stop %",      Group = "BE/Trail", DefaultValue = 0.8)]
        public double TrailPct { get; set; }

        // ── HTF Filter ───────────────────────────
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
        [Parameter("Daily Loss Limit %",Group = "FTMO", DefaultValue = 4.0)]
        public double DailyLossLimitPct { get; set; }
        [Parameter("Max Drawdown %",    Group = "FTMO", DefaultValue = 9.0)]
        public double MaxDdPct { get; set; }
        [Parameter("Session Start UTC", Group = "FTMO", DefaultValue = 7)]
        public int SessionStart { get; set; }
        [Parameter("Session End UTC",   Group = "FTMO", DefaultValue = 18)]
        public int SessionEnd { get; set; }

        // ── Logging ──────────────────────────────────────────────────────────
        [Parameter("KeepAlive (hour)", Group = "Logging", DefaultValue = 12.0, MinValue = 0.0)]
        public double LogKeepAliveHours { get; set; }
        [Parameter("KeepAlive Message", Group = "Logging", DefaultValue = "# CBot is running #")]
        public string LogKeepAliveMessage { get; set; }

        // ────────────────────────────────────────────────────────────────────
        //  Private state
        // ────────────────────────────────────────────────────────────────────

        private class SwingState
        {
            public bool   WaitingRetest;
            public double BreakoutLevel;
            public int    BarsSince;
        }

        private int[]        _swingLevels;
        private SwingState[] _states;

        // HTF Variables
        private Bars _w1;
        private Bars _h4;
        private ExponentialMovingAverage _w1Ema;
        private ExponentialMovingAverage _h4Ema;
        private DirectionalMovementSystem _h4Adx;

        // BE tracking per position
        private Dictionary<long, bool> _beTriggered = new Dictionary<long, bool>();

        // FTMO
        private double   _initialBalance;
        private double   _dayStartBalance;
        private int      _tradesToday;
        private DateTime _currentDay;
        private DateTime _lastLogTime;

        private const string LABEL = "BRACT-CS";

        // ────────────────────────────────────────────────────────────────────
        //  Lifecycle
        // ────────────────────────────────────────────────────────────────────

        protected override void OnStart()
        {
            try
            {
                Print("[START] PURE C# Robot initializing...");

                _swingLevels = new[] { SL1, SL2, SL3, SL4, SL5, SL6 };
                _states = new SwingState[6];
                for (int i = 0; i < 6; i++) _states[i] = new SwingState();

                // 1. Load Weekly Bars & Force historical data loading
                _w1 = MarketData.GetBars(TimeFrame.Weekly);
                int attempts = 0;
                while (_w1.Count < W1EmaPeriod + 10 && attempts < 10)
                {
                    if (_w1.LoadMoreHistory() == 0) break;
                    attempts++;
                }

                // 2. Load H4 Bars & Force historical data loading
                _h4 = MarketData.GetBars(TimeFrame.Hour4);
                attempts = 0;
                while (_h4.Count < H4EmaPeriod + H4AdxPeriod + 10 && attempts < 10)
                {
                    if (_h4.LoadMoreHistory() == 0) break;
                    attempts++;
                }
                
                // Create indicators
                _w1Ema = Indicators.ExponentialMovingAverage(_w1.ClosePrices, W1EmaPeriod);
                _h4Ema = Indicators.ExponentialMovingAverage(_h4.ClosePrices, H4EmaPeriod);
                _h4Adx = Indicators.DirectionalMovementSystem(_h4, H4AdxPeriod);

                // Az induló egyenleget mostantól a paraméter határozza meg, így újraindításkor is megmarad az FTMO limit memóriája!
                _initialBalance  = InitialAccountSize;
                _dayStartBalance = Account.Balance;
                _tradesToday     = 0;
                _currentDay      = Server.Time.Date;
                _lastLogTime     = Server.Time;

                double dynamicMaxLot = MaxLot * (InitialAccountSize / 25000.0);
                Print($"[START] Account Size: {InitialAccountSize} USD | Scaled Max Position: {Math.Round(dynamicMaxLot, 2)} Lot (Base: {MaxLot} Lot)");

                Positions.Closed += OnPositionsClosed;

                Print($"[START] XAUUSD-Active PURE C# | SL={SlPct}% TP={TpPct}% RR={TpPct/SlPct:F1}:1");
            }
            catch (Exception ex)
            {
                Print($"[CRITICAL ERROR IN ON_START] {ex}");
            }
        }

        protected override void OnStop()
        {
            Positions.Closed -= OnPositionsClosed;
        }

        // ────────────────────────────────────────────────────────────────────
        //  OnBar
        // ────────────────────────────────────────────────────────────────────

        protected override void OnBar()
        {
            try
            {
                if (!PassesFtmo())  return;
                if (!IsInSession()) return;
                if (!IsHtfBull())   return;

                // Kötés ha van szabad slot
                int openCount = Positions.FindAll(LABEL, SymbolName).Length;
                if (openCount >= MaxConcurrent) return;

                TryEntry(MaxConcurrent - openCount);
            }
            catch (Exception ex)
            {
                Print($"[ERROR IN ON_BAR] {ex}");
            }
        }

        // ────────────────────────────────────────────────────────────────────
        //  OnTick – BE + trailing + Drawdown Vészfék
        // ────────────────────────────────────────────────────────────────────

        protected override void OnTick()
        {
            // Keep Alive logolás (tizedes óra alapján)
            if (LogKeepAliveHours > 0 && !string.IsNullOrEmpty(LogKeepAliveMessage))
            {
                if ((Server.Time - _lastLogTime).TotalHours >= LogKeepAliveHours)
                {
                    _lastLogTime = Server.Time;
                    string msg = LogKeepAliveMessage.Length > 256 ? LogKeepAliveMessage.Substring(0, 256) : LogKeepAliveMessage;
                    Print(msg);
                }
            }

            // Napi reset áthelyezése OnTick-be a hajszálpontos éjféli egyenleghez
            var today = Server.Time.Date;
            if (today > _currentDay)
            {
                _currentDay      = today;
                _tradesToday     = 0;
                _dayStartBalance = Account.Balance;
            }

            // --- FTMO VÉSZFÉK (Hard Stop) ---
            // A megengedett dollár veszteség az Initial Balance-ből számolódik
            double currentDayLoss = ((_dayStartBalance - Account.Equity) / _initialBalance) * 100.0;
            
            // FTMO Max Loss az INITIAL (Induló) egyenlegre vonatkozik, NEM a csúcsra!
            double currentDd = (_initialBalance - Account.Equity) / _initialBalance * 100.0;

            if (currentDayLoss >= DailyLossLimitPct || currentDd >= MaxDdPct)
            {
                Print($"[VÉSZFÉK] Drawdown limit elérve (DayLoss: {currentDayLoss:F2}%, DD: {currentDd:F2}%)! Kötések zárása.");
                foreach (var p in Positions.FindAll(LABEL, SymbolName))
                {
                    ClosePosition(p);
                }
                return; // Ne is futtasson tovább semmit
            }
            // --------------------------------

            foreach (var pos in Positions.FindAll(LABEL, SymbolName))
                ManagePosition(pos);
        }

        private void OnPositionsClosed(PositionClosedEventArgs args)
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
                        var r = ModifyPosition(pos, newSL, pos.TakeProfit, ProtectionType.Absolute);
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
                    ModifyPosition(pos, newSL, pos.TakeProfit, ProtectionType.Absolute);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  HTF BIAS – EXACT MATCH WITH PYTHON
        // ════════════════════════════════════════════════════════════════════

        private bool IsHtfBull()
        {
            if (_w1 == null || _h4 == null) return false;
            
            if (_w1.Count < W1EmaPeriod + 5) return false;
            if (_h4.Count < H4EmaPeriod + H4AdxPeriod + 5) return false;

            // L1: W1 close > W1 EMA10
            if (_w1.ClosePrices.Last(1) <= _w1Ema.Result.Last(2)) return false;

            // L2: W1 momentum
            if (_w1.ClosePrices.Last(1) <= _w1.ClosePrices.Last(2)) return false;

            // L3: H4 close > H4 EMA20
            if (_h4.ClosePrices.Last(1) <= _h4Ema.Result.Last(1)) return false;

            // L4: H4 ADX ≥ Threshold
            if (_h4Adx.ADX.Last(1) < AdxThreshold) return false;

            return true;
        }

        // ════════════════════════════════════════════════════════════════════
        //  FTMO + SESSION
        // ════════════════════════════════════════════════════════════════════

        private bool PassesFtmo()
        {
            if (_tradesToday >= MaxTradesPerDay) return false;

            // Napi veszteség FTMO szabály szerint (dollár limit az Initial Balance alapján)
            double dayLoss = ((_dayStartBalance - Account.Equity) / _initialBalance) * 100.0;
            if (dayLoss >= DailyLossLimitPct)
            {
                return false;
            }

            // Max DD az induló egyenleg (Initial Balance) alapján, FTMO szabály szerint
            double dd = (_initialBalance - Account.Equity) / _initialBalance * 100.0;
            if (dd >= MaxDdPct)
            {
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
            // A kockázat (Risk) mindig a paraméterben megadott induló tőke X %-a marad (Fix USD kockázat)
            double risk  = InitialAccountSize * (RiskPct / 100.0);
            double uppp  = Symbol.TickValue / Symbol.TickSize;
            if (uppp <= 0) return 0;
            
            double dynamicMaxLot = MaxLot * (InitialAccountSize / 25000.0);
            double maxUnits = Symbol.QuantityToVolumeInUnits(dynamicMaxLot);
            double units = Math.Min(risk / (slDist * uppp), maxUnits);
            
            return Symbol.NormalizeVolumeInUnits(units, RoundingMode.Down);
        }

        private double Round(double price)
        {
            double t = Symbol.TickSize;
            return Math.Round(price / t) * t;
        }
    }
}
