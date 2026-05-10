using System;
using System.Linq;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Collections;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum RiskType
    {
        FixedLots,
        RiskPercentage
    }

    [Robot(AccessRights = AccessRights.None, AddIndicators = true)]
    public class SwingTraderBot : Robot
    {
        // --- Trading & Risk ---
        [Parameter("Risk Calculation Mode", Group = "Trading", DefaultValue = RiskType.RiskPercentage)]
        public RiskType RiskMode { get; set; }

        [Parameter("Risk Per Trade (%)", Group = "Trading", DefaultValue = 1.5, MinValue = 0.1)]
        public double RiskPerTradePct { get; set; }

        [Parameter("Fixed Volume (Lots)", Group = "Trading", DefaultValue = 0.1, MinValue = 0.01)]
        public double VolumeInLots { get; set; }

        [Parameter("Take Profit (R/R)", Group = "Trading", DefaultValue = 3.0, MinValue = 1.0)]
        public double TakeProfitRR { get; set; }

        // --- Smart Money Entry Logic ---
        [Parameter("Momentum Candle (ATR Multiplier)", Group = "Smart Money", DefaultValue = 1.5, MinValue = 0.5)]
        public double MomentumCandleAtrMultiplier { get; set; }

        [Parameter("ATR Period", Group = "Smart Money", DefaultValue = 14, MinValue = 1)]
        public int AtrPeriod { get; set; }

        [Parameter("Max Pending Orders", Group = "Smart Money", DefaultValue = 1, MinValue = 1)]
        public int MaxPendingOrders { get; set; }
        
        [Parameter("Order Expiration (Bars)", Group = "Smart Money", DefaultValue = 12, MinValue = 1)]
        public int OrderExpirationBars { get; set; }

        // --- Trend Filters ---
        [Parameter("H1 Trend Filter", Group = "Trend Filters", DefaultValue = true)]
        public bool EnableH1Filter { get; set; }

        [Parameter("H4 Trend Filter", Group = "Trend Filters", DefaultValue = true)]
        public bool EnableH4Filter { get; set; }

        // --- Position Management ---
        [Parameter("Enable Break-Even", Group = "Management", DefaultValue = true)]
        public bool EnableBreakEven { get; set; }

        [Parameter("Break-Even Trigger (R/R)", Group = "Management", DefaultValue = 1.5, MinValue = 0.5)]
        public double BreakEvenTriggerRR { get; set; }

        [Parameter("Enable Trailing Stop", Group = "Management", DefaultValue = true)]
        public bool EnableTrailingStop { get; set; }

        [Parameter("Trailing Stop Trigger (R/R)", Group = "Management", DefaultValue = 2.0, MinValue = 0.5)]
        public double TrailingStopTriggerRR { get; set; }

        [Parameter("Trailing Stop Distance (R/R)", Group = "Management", DefaultValue = 1.0, MinValue = 0.1)]
        public double TrailingStopDistanceRR { get; set; }

        // --- Session Filter ---
        [Parameter("Enable Session Filter", Group = "Trading Hours", DefaultValue = true)]
        public bool EnableSessionFilter { get; set; }

        [Parameter("Session Start Hour (0-23)", Group = "Trading Hours", DefaultValue = 8, MinValue = 0, MaxValue = 23)]
        public int SessionStartHour { get; set; }

        [Parameter("Session End Hour (0-23)", Group = "Trading Hours", DefaultValue = 17, MinValue = 0, MaxValue = 23)]
        public int SessionEndHour { get; set; }

        // --- FTMO Drawdown Limits ---
        [Parameter("Initial Account Size (USD)", Group = "FTMO & Risk", DefaultValue = 10000.0)]
        public double InitialAccountSize { get; set; }

        [Parameter("Daily Loss Limit %", Group = "FTMO & Risk", DefaultValue = 4.0)]
        public double DailyLossLimitPct { get; set; }

        [Parameter("Max Total Loss %", Group = "FTMO & Risk", DefaultValue = 9.0)]
        public double MaxDdPct { get; set; }

        [Parameter("Max Trades Per Day", Group = "FTMO & Risk", DefaultValue = 5, MinValue = 1)]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Keep Alive Interval (Min, 0=Off)", Group = "Logging", DefaultValue = 60, MinValue = 0)]
        public int KeepAliveIntervalMin { get; set; }


        private AverageTrueRange _atr;
        private Bars _h1Bars;
        private ExponentialMovingAverage _h1Ema50;
        private ExponentialMovingAverage _h1Ema200;
        private Bars _h4Bars;
        private ExponentialMovingAverage _h4Ema50;

        // FTMO / Intraday State
        private double _initialBalance;
        private double _dayStartBalance;
        private DateTime _currentDay;
        private int _tradesOpenedToday;
        private bool _tradingStoppedForDay;

        // State to remember SL pips for R/R calculations
        private Dictionary<int, double> _positionSlPips = new Dictionary<int, double>();

        protected override void OnStart()
        {
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Simple);

            if (EnableH1Filter)
            {
                _h1Bars = MarketData.GetBars(TimeFrame.Hour);
                _h1Ema50 = Indicators.ExponentialMovingAverage(_h1Bars.ClosePrices, 50);
                _h1Ema200 = Indicators.ExponentialMovingAverage(_h1Bars.ClosePrices, 200);
            }

            if (EnableH4Filter)
            {
                _h4Bars = MarketData.GetBars(TimeFrame.Hour4);
                _h4Ema50 = Indicators.ExponentialMovingAverage(_h4Bars.ClosePrices, 50);
            }

            // FTMO Initialization
            _initialBalance = InitialAccountSize;
            _dayStartBalance = Account.Balance;
            _currentDay = Server.Time.Date;
            _tradesOpenedToday = 0;
            _tradingStoppedForDay = false;

            Positions.Closed += OnPositionsClosed;
            Positions.Opened += OnPositionOpened;

            Print("=================================================");
            Print("[START] Smart Money SwingTraderBot elindult.");
            Print($"Mód: FVG Retest | H1 Trend: {EnableH1Filter} | H4 Trend: {EnableH4Filter}");
            Print($"Kockázat: {RiskPerTradePct}% | Cél: {TakeProfitRR} R/R");
            Print("=================================================");

            if (KeepAliveIntervalMin > 0)
            {
                Timer.Start(KeepAliveIntervalMin * 60);
            }
        }

        protected override void OnTimer()
        {
            Print($"[KEEP ALIVE] Bot fut | Equity: {Math.Round(Account.Equity, 2)}");
        }

        protected override void OnStop()
        {
            Positions.Closed -= OnPositionsClosed;
            Positions.Opened -= OnPositionOpened;
            Print("[STOP] SwingTraderBot leállt.");
        }

        protected override void OnTick()
        {
            // Éjféli Reset
            var today = Server.Time.Date;
            if (today > _currentDay)
            {
                _currentDay = today;
                _dayStartBalance = Account.Balance;
                _tradesOpenedToday = 0;
                _tradingStoppedForDay = false;
                Print($"[RESET] Új nap. DayStartBalance: {_dayStartBalance}");
            }

            // FTMO Drawdown Vészfék
            double maxDailyLossAmount = _initialBalance * (DailyLossLimitPct / 100.0);
            double dailyLossLimitLevel = _dayStartBalance - maxDailyLossAmount;
            double maxTotalLossAmount = _initialBalance * (MaxDdPct / 100.0);
            double totalLossLimitLevel = _initialBalance - maxTotalLossAmount;

            if (Account.Equity <= dailyLossLimitLevel || Account.Equity <= totalLossLimitLevel)
            {
                if (!_tradingStoppedForDay)
                {
                    Print($"[VÉSZFÉK] FTMO Drawdown limit elérve! Equity: {Account.Equity}. Kötések zárása...");
                    _tradingStoppedForDay = true;
                    foreach (var p in Positions.Where(x => x.SymbolName == SymbolName).ToArray()) ClosePosition(p);
                    foreach (var o in PendingOrders.Where(x => x.SymbolName == SymbolName).ToArray()) CancelPendingOrder(o);
                }
            }

            // Position Management (Break-Even & Trailing Stop) based on R/R
            foreach (var position in Positions.Where(x => x.SymbolName == SymbolName).ToArray())
            {
                if (!_positionSlPips.ContainsKey(position.Id)) continue;
                
                double initialSlPips = _positionSlPips[position.Id];
                if (initialSlPips <= 0) continue;

                double currentRR = position.Pips / initialSlPips;

                // Break-Even
                if (EnableBreakEven && currentRR >= BreakEvenTriggerRR)
                {
                    double bePrice = position.EntryPrice;
                    if (position.TradeType == TradeType.Buy && (position.StopLoss == null || position.StopLoss < bePrice))
                    {
                        ModifyPosition(position, bePrice, position.TakeProfit);
                        Print($"[Break-Even] Vétel levédve nullába. {Math.Round(currentRR, 2)}R elérésekor.");
                    }
                    else if (position.TradeType == TradeType.Sell && (position.StopLoss == null || position.StopLoss > bePrice))
                    {
                        ModifyPosition(position, bePrice, position.TakeProfit);
                        Print($"[Break-Even] Eladás levédve nullába. {Math.Round(currentRR, 2)}R elérésekor.");
                    }
                }

                // Trailing Stop (R/R based)
                if (EnableTrailingStop && currentRR >= TrailingStopTriggerRR)
                {
                    double trailDistancePips = initialSlPips * TrailingStopDistanceRR;
                    double newStopLoss = position.TradeType == TradeType.Buy
                        ? Symbol.Bid - (trailDistancePips * Symbol.PipSize)
                        : Symbol.Ask + (trailDistancePips * Symbol.PipSize);

                    bool shouldModify = false;
                    if (position.TradeType == TradeType.Buy && (position.StopLoss == null || newStopLoss > position.StopLoss))
                    {
                        shouldModify = true;
                    }
                    else if (position.TradeType == TradeType.Sell && (position.StopLoss == null || newStopLoss < position.StopLoss))
                    {
                        shouldModify = true;
                    }

                    if (shouldModify)
                    {
                        ModifyPosition(position, newStopLoss, position.TakeProfit);
                    }
                }
            }
        }

        private void OnPositionOpened(PositionOpenedEventArgs args)
        {
            if (args.Position.SymbolName != SymbolName) return;

            _tradesOpenedToday++;
            
            // Calculate and store the initial SL in pips for R/R management
            double slPips = 0;
            if (args.Position.StopLoss.HasValue)
            {
                slPips = Math.Abs(args.Position.EntryPrice - args.Position.StopLoss.Value) / Symbol.PipSize;
            }
            _positionSlPips[args.Position.Id] = slPips;

            Print($"[NYITÁS] {args.Position.TradeType} | Ár: {args.Position.EntryPrice} | SL Pips: {Math.Round(slPips, 1)}");
        }

        private void OnPositionsClosed(PositionClosedEventArgs args)
        {
            if (args.Position.SymbolName != SymbolName) return;
            _positionSlPips.Remove(args.Position.Id);
            Print($"[ZÁRÁS] {args.Position.TradeType} | P&L: {Math.Round(args.Position.NetProfit, 2)}");
        }

        private double CalculateVolume(double stopLossPips)
        {
            if (RiskMode == RiskType.FixedLots) return Symbol.NormalizeVolumeInUnits(Symbol.QuantityToVolumeInUnits(VolumeInLots));

            double riskAmount = Account.Balance * (RiskPerTradePct / 100.0);
            double exactVolume = riskAmount / (stopLossPips * Symbol.PipValue);
            double normalizedVolume = Symbol.NormalizeVolumeInUnits(exactVolume, RoundingMode.Down);
            if (normalizedVolume == 0) normalizedVolume = Symbol.VolumeInUnitsMin;
            return normalizedVolume;
        }

        protected override void OnBar()
        {
            if (_tradingStoppedForDay || _tradesOpenedToday >= MaxTradesPerDay) return;
            if (Positions.Count(x => x.SymbolName == SymbolName) > 0) return;

            // Session Filter
            if (EnableSessionFilter)
            {
                int currentHour = Server.Time.Hour;
                bool isWithinSession = (SessionStartHour <= SessionEndHour) 
                    ? (currentHour >= SessionStartHour && currentHour < SessionEndHour)
                    : (currentHour >= SessionStartHour || currentHour < SessionEndHour);

                if (!isWithinSession)
                {
                    foreach (var o in PendingOrders.Where(x => x.SymbolName == SymbolName).ToArray()) CancelPendingOrder(o);
                    return; 
                }
            }

            // Clean up expired pending orders
            foreach (var order in PendingOrders.Where(x => x.SymbolName == SymbolName).ToArray())
            {
                if (order.ExpirationTime.HasValue && order.ExpirationTime.Value <= Server.Time)
                {
                    CancelPendingOrder(order);
                }
            }

            // Only allow MaxPendingOrders
            if (PendingOrders.Count(x => x.SymbolName == SymbolName) >= MaxPendingOrders) return;

            // HTF Trend Check
            bool h1Uptrend = true; bool h1Downtrend = true;
            if (EnableH1Filter && _h1Bars != null)
            {
                double h1Close = _h1Bars.ClosePrices.Last(1);
                h1Uptrend = h1Close > _h1Ema50.Result.Last(1) && _h1Ema50.Result.Last(1) > _h1Ema200.Result.Last(1);
                h1Downtrend = h1Close < _h1Ema50.Result.Last(1) && _h1Ema50.Result.Last(1) < _h1Ema200.Result.Last(1);
            }

            bool h4Uptrend = true; bool h4Downtrend = true;
            if (EnableH4Filter && _h4Bars != null)
            {
                double h4Close = _h4Bars.ClosePrices.Last(1);
                h4Uptrend = h4Close > _h4Ema50.Result.Last(1);
                h4Downtrend = h4Close < _h4Ema50.Result.Last(1);
            }

            bool isGlobalUptrend = h1Uptrend && h4Uptrend;
            bool isGlobalDowntrend = h1Downtrend && h4Downtrend;

            if (!isGlobalUptrend && !isGlobalDowntrend) return;

            // FVG and Momentum Detection
            if (Bars.Count < 4) return;
            
            double currentAtr = _atr.Result.Last(1);
            
            // Candle 1 (Last 3)
            // Candle 2 (Last 2) - Momentum Candle
            // Candle 3 (Last 1) - Just closed
            
            double body2 = Math.Abs(Bars.ClosePrices.Last(2) - Bars.OpenPrices.Last(2));
            bool isStrongCandle = body2 > currentAtr * MomentumCandleAtrMultiplier;

            if (!isStrongCandle) return;

            // Bullish FVG Check
            if (isGlobalUptrend && Bars.ClosePrices.Last(2) > Bars.OpenPrices.Last(2))
            {
                if (Bars.LowPrices.Last(1) > Bars.HighPrices.Last(3))
                {
                    double fvgTop = Bars.LowPrices.Last(1);
                    double fvgBottom = Bars.HighPrices.Last(3);
                    double entryPrice = fvgTop; // Entry at the top of the FVG

                    // SL below the origin of the move
                    double slPrice = Math.Min(Bars.LowPrices.Last(3), fvgTop - (currentAtr * 1.5));
                    double slPips = (entryPrice - slPrice) / Symbol.PipSize;
                    
                    if (slPips < 10) slPips = 10; // Minimum 10 pips SL

                    double tpPrice = entryPrice + (slPips * TakeProfitRR * Symbol.PipSize);
                    double tpPips = (tpPrice - entryPrice) / Symbol.PipSize;

                    double volume = CalculateVolume(slPips);
                    DateTime expiration = Server.Time.AddMinutes(OrderExpirationBars * Bars.TimeFrame.ToMinutes());

                    PlaceLimitOrder(TradeType.Buy, SymbolName, volume, entryPrice, "FVG_Buy", slPips, tpPips, expiration);
                    Print($"[FVG] Bullish FVG észlelve. Buy Limit: {entryPrice} | SL: {slPrice} ({Math.Round(slPips,1)} pips)");
                }
            }
            
            // Bearish FVG Check
            if (isGlobalDowntrend && Bars.ClosePrices.Last(2) < Bars.OpenPrices.Last(2))
            {
                if (Bars.HighPrices.Last(1) < Bars.LowPrices.Last(3))
                {
                    double fvgBottom = Bars.HighPrices.Last(1);
                    double fvgTop = Bars.LowPrices.Last(3);
                    double entryPrice = fvgBottom; // Entry at the bottom of the FVG

                    // SL above the origin of the move
                    double slPrice = Math.Max(Bars.HighPrices.Last(3), fvgBottom + (currentAtr * 1.5));
                    double slPips = (slPrice - entryPrice) / Symbol.PipSize;
                    
                    if (slPips < 10) slPips = 10;

                    double tpPrice = entryPrice - (slPips * TakeProfitRR * Symbol.PipSize);
                    double tpPips = (entryPrice - tpPrice) / Symbol.PipSize;

                    double volume = CalculateVolume(slPips);
                    DateTime expiration = Server.Time.AddMinutes(OrderExpirationBars * Bars.TimeFrame.ToMinutes());

                    PlaceLimitOrder(TradeType.Sell, SymbolName, volume, entryPrice, "FVG_Sell", slPips, tpPips, expiration);
                    Print($"[FVG] Bearish FVG észlelve. Sell Limit: {entryPrice} | SL: {slPrice} ({Math.Round(slPips,1)} pips)");
                }
            }
        }
    }
}