using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.CentralEuropeStandardTime, AccessRights = AccessRights.None)]
    public class FtmoSwing8020Cbot : Robot
    {
        private const string BotLabel = "FTMO_SWING_8020";
        [Parameter("Initial Simulated Capital", DefaultValue = 100000.0, MinValue = 10000.0, MaxValue = 200000.0, Step = 1000.0)]
        public double InitialSimulatedCapital { get; set; }

        [Parameter("Risk Per Trade %", DefaultValue = 0.50, MinValue = 0.05, MaxValue = 3.0, Step = 0.05)]
        public double RiskPerTradePercent { get; set; }

        [Parameter("Max Daily Loss %", DefaultValue = 5.0, MinValue = 1.0, MaxValue = 10.0, Step = 0.25)]
        public double MaxDailyLossPercent { get; set; }

        [Parameter("Max Total Drawdown %", DefaultValue = 10.0, MinValue = 2.0, MaxValue = 20.0, Step = 0.25)]
        public double MaxTotalDrawdownPercent { get; set; }

        [Parameter("Reward / Risk", DefaultValue = 3.0, MinValue = 1.0, MaxValue = 10.0, Step = 0.25)]
        public double RewardRiskMultiple { get; set; }

        [Parameter("Macro Trend EMA (H1)", DefaultValue = 50, MinValue = 10, MaxValue = 200)]
        public int MacroEmaPeriod { get; set; }

        [Parameter("Trend EMA Fast", DefaultValue = 20, MinValue = 5, MaxValue = 100)]
        public int FastEmaPeriod { get; set; }

        [Parameter("Trend EMA Slow", DefaultValue = 50, MinValue = 10, MaxValue = 200)]
        public int SlowEmaPeriod { get; set; }

        [Parameter("ATR Period", DefaultValue = 14, MinValue = 5, MaxValue = 50)]
        public int AtrPeriod { get; set; }

        [Parameter("Structure Lookback Bars", DefaultValue = 12, MinValue = 4, MaxValue = 48)]
        public int StructureLookbackBars { get; set; }

        [Parameter("Level Tolerance (pts)", DefaultValue = 8.0, MinValue = 2.0, MaxValue = 30.0, Step = 0.5)]
        public double LevelTolerancePoints { get; set; }

        [Parameter("Entry Buffer (pts)", DefaultValue = 4.0, MinValue = 1.0, MaxValue = 20.0, Step = 0.5)]
        public double EntryBufferPoints { get; set; }

        [Parameter("Max Spread (pts)", DefaultValue = 6.0, MinValue = 1.0, MaxValue = 25.0, Step = 0.5)]
        public double MaxSpreadPoints { get; set; }

        [Parameter("Min Stop Loss (pts)", DefaultValue = 15.0, MinValue = 5.0, MaxValue = 100.0, Step = 1.0)]
        public double MinStopLossPoints { get; set; }

        [Parameter("Trade Cooldown (mins)", DefaultValue = 60, MinValue = 0, MaxValue = 300)]
        public int TradeCooldownMinutes { get; set; }

        [Parameter("Max Trades Per Day", DefaultValue = 3, MinValue = 1, MaxValue = 20)]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Break Even At R", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 5.0, Step = 0.25)]
        public double BreakEvenAtR { get; set; }

        [Parameter("Trail After R", DefaultValue = 2.0, MinValue = 1.0, MaxValue = 5.0, Step = 0.25)]
        public double TrailAfterR { get; set; }

        [Parameter("Allow Long", DefaultValue = true)]
        public bool AllowLong { get; set; }

        [Parameter("Allow Short", DefaultValue = true)]
        public bool AllowShort { get; set; }

        [Parameter("Use Session Filter", DefaultValue = true)]
        public bool UseSessionFilter { get; set; }

        [Parameter("Session Start Hour", DefaultValue = 14, MinValue = 0, MaxValue = 23)]
        public int SessionStartHour { get; set; }

        [Parameter("Session End Hour", DefaultValue = 21, MinValue = 0, MaxValue = 23)]
        public int SessionEndHour { get; set; }

        private Bars _tenMinuteBars;
        private Bars _h1Bars;
        private ExponentialMovingAverage _fastEma;
        private ExponentialMovingAverage _slowEma;
        private ExponentialMovingAverage _macroEma;
        private AverageTrueRange _atr;

        private SyntheticBar _active200SecondBar;
        private SyntheticBar _lastClosed200SecondBar;
        private DateTime _currentBarStart;

        private DateTime _ftmoTradingDate;
        private double _ftmoMidnightBalance;
        private double _initialBalance;
        private bool _dailyLossLockTriggered;
        private bool _maxLossLockTriggered;
        private string _lastRiskLockReason;
        
        private DateTime _lastTradeClosureTime;
        private int _dailyTradeCount;

        protected override void OnStart()
        {
            _tenMinuteBars = MarketData.GetBars(TimeFrame.Minute10, SymbolName);
            _h1Bars = MarketData.GetBars(TimeFrame.Hour, SymbolName);
            _fastEma = Indicators.ExponentialMovingAverage(_tenMinuteBars.ClosePrices, FastEmaPeriod);
            _slowEma = Indicators.ExponentialMovingAverage(_tenMinuteBars.ClosePrices, SlowEmaPeriod);
            _macroEma = Indicators.ExponentialMovingAverage(_h1Bars.ClosePrices, MacroEmaPeriod);
            _atr = Indicators.AverageTrueRange(_tenMinuteBars, AtrPeriod, MovingAverageType.Exponential);

            // A Backtester kompatibilitás miatt az Account.Balance-t használjuk kezdőtőkeként,
            // így elkerüljük az azonnali FTMO kizárást egy kisebb backtest számlán.
            _initialBalance = Account.Balance;
            _ftmoTradingDate = Server.Time.Date;
            _ftmoMidnightBalance = ResolveFtmoMidnightBalance(_ftmoTradingDate);

            _currentBarStart = AlignTo200SecondWindow(Server.Time);
            _active200SecondBar = new SyntheticBar(_currentBarStart, Symbol.Bid);

            _dailyTradeCount = CalculateTodayTradeCount();
            Positions.Closed += OnPositionClosed;

            Print("FTMO Swing 8020 cBot started on {0}. FTMO midnight balance: {1}. Initial simulated capital: {2}", SymbolName, _ftmoMidnightBalance, _initialBalance);
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (args.Position.SymbolName == SymbolName && args.Position.Label == BotLabel)
            {
                _lastTradeClosureTime = Server.Time;
            }
        }

        private int CalculateTodayTradeCount()
        {
            int count = 0;
            foreach (var trade in History)
            {
                if (trade.SymbolName == SymbolName && trade.Label == BotLabel && trade.EntryTime.Date == Server.Time.Date)
                    count++;
            }
            foreach (var pos in Positions)
            {
                if (pos.SymbolName == SymbolName && pos.Label == BotLabel && pos.EntryTime.Date == Server.Time.Date)
                    count++;
            }
            return count;
        }

        protected override void OnTick()
        {
            RollFtmoDayIfNeeded();
            UpdateRiskLock();

            if (HandleRiskLock())
                return;

            UpdateSyntheticBar();
            ManageOpenPositions();
        }

        private void UpdateSyntheticBar()
        {
            var tickTime = Server.Time;
            var bid = Symbol.Bid;

            if (tickTime < _currentBarStart.AddSeconds(200))
            {
                _active200SecondBar.Update(bid);
                return;
            }

            _lastClosed200SecondBar = _active200SecondBar;
            _currentBarStart = AlignTo200SecondWindow(tickTime);
            _active200SecondBar = new SyntheticBar(_currentBarStart, bid);
            _active200SecondBar.Update(bid);

            EvaluateEntryOnClosed200SecondBar();
        }

        private void EvaluateEntryOnClosed200SecondBar()
        {
            if (_lastClosed200SecondBar == null || IsTradingLocked() || HasOpenBotPosition())
                return;

            if (UseSessionFilter)
            {
                var currentHour = Server.Time.Hour;
                if (SessionStartHour < SessionEndHour)
                {
                    if (currentHour < SessionStartHour || currentHour >= SessionEndHour)
                        return;
                }
                else
                {
                    if (currentHour < SessionStartHour && currentHour >= SessionEndHour)
                        return;
                }
            }

            if (_dailyTradeCount >= MaxTradesPerDay)
                return;

            if (TradeCooldownMinutes > 0 && (Server.Time - _lastTradeClosureTime).TotalMinutes < TradeCooldownMinutes)
                return;

            if (_tenMinuteBars.Count < Math.Max(SlowEmaPeriod + 5, StructureLookbackBars + 5))
                return;

            var spreadPoints = Symbol.Spread / Symbol.PipSize;
            if (spreadPoints > MaxSpreadPoints)
                return;

            var trend = GetTrendBias();
            if (trend == TradeType.Buy && AllowLong && IsLongSetup(_lastClosed200SecondBar))
            {
                PlaceTrade(TradeType.Buy, _lastClosed200SecondBar);
            }
            else if (trend == TradeType.Sell && AllowShort && IsShortSetup(_lastClosed200SecondBar))
            {
                PlaceTrade(TradeType.Sell, _lastClosed200SecondBar);
            }
        }

        private TradeType? GetTrendBias()
        {
            var index = _tenMinuteBars.ClosePrices.Count - 2;
            if (index < 2 || _h1Bars.ClosePrices.Count < 2)
                return null;

            var close = _tenMinuteBars.ClosePrices[index];
            var fast = _fastEma.Result[index];
            var slow = _slowEma.Result[index];
            var atr = _atr.Result[index];

            var h1Index = _h1Bars.ClosePrices.Count - 2;
            var macroEmaValue = _macroEma.Result[h1Index];
            var isMacroBullish = close > macroEmaValue;
            var isMacroBearish = close < macroEmaValue;

            var highest = double.MinValue;
            var lowest = double.MaxValue;

            for (var i = index; i > index - StructureLookbackBars; i--)
            {
                highest = Math.Max(highest, _tenMinuteBars.HighPrices[i]);
                lowest = Math.Min(lowest, _tenMinuteBars.LowPrices[i]);
            }

            var structureRange = Math.Max(highest - lowest, Symbol.PipSize * 10);
            var bullishStructure = close >= lowest + structureRange * 0.55;
            var bearishStructure = close <= lowest + structureRange * 0.45;

            if (close > fast && fast > slow && atr > Symbol.PipSize * 8 && bullishStructure && isMacroBullish)
                return TradeType.Buy;

            if (close < fast && fast < slow && atr > Symbol.PipSize * 8 && bearishStructure && isMacroBearish)
                return TradeType.Sell;

            return null;
        }

        private bool IsLongSetup(SyntheticBar bar)
        {
            var level = GetNearest8020Level(bar.Low);
            var tolerance = LevelTolerancePoints * Symbol.PipSize;
            var rejection = bar.Close > level && bar.Low <= level + tolerance;
            var bodyStrength = bar.Close > bar.Open;
            var closeNearHigh = (bar.High - bar.Close) <= (bar.Range * 0.35);

            return rejection && bodyStrength && closeNearHigh;
        }

        private bool IsShortSetup(SyntheticBar bar)
        {
            var level = GetNearest8020Level(bar.High);
            var tolerance = LevelTolerancePoints * Symbol.PipSize;
            var rejection = bar.Close < level && bar.High >= level - tolerance;
            var bodyStrength = bar.Close < bar.Open;
            var closeNearLow = (bar.Close - bar.Low) <= (bar.Range * 0.35);

            return rejection && bodyStrength && closeNearLow;
        }

        private void PlaceTrade(TradeType tradeType, SyntheticBar signalBar)
        {
            var buffer = EntryBufferPoints * Symbol.PipSize;
            var entryPrice = tradeType == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            var stopPrice = tradeType == TradeType.Buy ? signalBar.Low - buffer : signalBar.High + buffer;
            var stopDistance = Math.Abs(entryPrice - stopPrice);
            var stopPips = stopDistance / Symbol.PipSize;

            if (stopPips < MinStopLossPoints)
            {
                stopPips = MinStopLossPoints;
            }

            if (stopPips < 1 || stopPips > 300)
                return;

            var volume = CalculateRiskBasedVolume(stopPips);
            if (volume < Symbol.VolumeInUnitsMin)
                return;

            var takeProfitPips = stopPips * RewardRiskMultiple;

            var result = ExecuteMarketOrder(tradeType, SymbolName, volume, BotLabel, stopPips, takeProfitPips);
            if (!result.IsSuccessful)
            {
                Print("Order failed: {0}", result.Error);
                return;
            }

            _dailyTradeCount++;
            Print("{0} opened at {1} | SL {2} pips | TP {3} pips | volume {4}", tradeType, entryPrice, stopPips, takeProfitPips, volume);
        }

        private double CalculateRiskBasedVolume(double stopPips)
        {
            var riskAmount = Account.Balance * (RiskPerTradePercent / 100.0);
            var rawVolume = riskAmount / (stopPips * Symbol.PipValue);
            return Symbol.NormalizeVolumeInUnits(rawVolume, RoundingMode.Down);
        }

        private void ManageOpenPositions()
        {
            foreach (var position in Positions)
            {
                if (position.SymbolName != SymbolName || position.Label != BotLabel)
                    continue;

                var currentPrice = position.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;
                var riskDistance = Math.Abs(position.EntryPrice - position.StopLoss.GetValueOrDefault());
                if (riskDistance <= 0)
                    continue;

                var rewardDistance = position.TradeType == TradeType.Buy
                    ? currentPrice - position.EntryPrice
                    : position.EntryPrice - currentPrice;

                var achievedR = rewardDistance / riskDistance;

                if (achievedR >= BreakEvenAtR)
                    TryMoveToBreakEven(position);

                if (achievedR >= TrailAfterR && _lastClosed200SecondBar != null)
                    TryTrailBehindSignalBar(position, _lastClosed200SecondBar);
            }
        }

        private void TryMoveToBreakEven(Position position)
        {
            var buffer = Symbol.PipSize * 2;
            var targetStop = position.TradeType == TradeType.Buy
                ? position.EntryPrice + buffer
                : position.EntryPrice - buffer;

            if (position.StopLoss == null)
                return;

            if (position.TradeType == TradeType.Buy && targetStop > position.StopLoss)
                ModifyPosition(position, targetStop, position.TakeProfit, ProtectionType.Absolute);

            if (position.TradeType == TradeType.Sell && targetStop < position.StopLoss)
                ModifyPosition(position, targetStop, position.TakeProfit, ProtectionType.Absolute);
        }

        private void TryTrailBehindSignalBar(Position position, SyntheticBar bar)
        {
            var buffer = EntryBufferPoints * Symbol.PipSize;
            var targetStop = position.TradeType == TradeType.Buy
                ? bar.Low - buffer
                : bar.High + buffer;

            if (position.StopLoss == null)
                return;

            if (position.TradeType == TradeType.Buy && targetStop > position.StopLoss && targetStop < Symbol.Bid)
                ModifyPosition(position, targetStop, position.TakeProfit, ProtectionType.Absolute);

            if (position.TradeType == TradeType.Sell && targetStop < position.StopLoss && targetStop > Symbol.Ask)
                ModifyPosition(position, targetStop, position.TakeProfit, ProtectionType.Absolute);
        }

        private bool HasOpenBotPosition()
        {
            foreach (var position in Positions)
            {
                if (position.SymbolName == SymbolName && position.Label == BotLabel)
                    return true;
            }

            return false;
        }

        private void RollFtmoDayIfNeeded()
        {
            if (Server.Time.Date == _ftmoTradingDate)
                return;

            _ftmoTradingDate = Server.Time.Date;
            _ftmoMidnightBalance = ResolveFtmoMidnightBalance(_ftmoTradingDate);
            _dailyLossLockTriggered = false;
            _lastRiskLockReason = null;
            _dailyTradeCount = 0;

            Print("New FTMO day. Midnight balance reset to {0}", _ftmoMidnightBalance);
        }

        private void UpdateRiskLock()
        {
            var dailyLossFloor = _ftmoMidnightBalance - (_initialBalance * (MaxDailyLossPercent / 100.0));
            var maxLossFloor = _initialBalance - (_initialBalance * (MaxTotalDrawdownPercent / 100.0));

            if (Account.Equity <= dailyLossFloor)
                _dailyLossLockTriggered = true;

            if (Account.Equity <= maxLossFloor)
                _maxLossLockTriggered = true;
        }

        private DateTime AlignTo200SecondWindow(DateTime time)
        {
            var totalSeconds = time.Hour * 3600 + time.Minute * 60 + time.Second;
            var bucket = (totalSeconds / 200) * 200;
            var alignedHour = bucket / 3600;
            var alignedMinute = (bucket % 3600) / 60;
            var alignedSecond = bucket % 60;

            return new DateTime(time.Year, time.Month, time.Day, alignedHour, alignedMinute, alignedSecond, time.Kind);
        }

        private bool HandleRiskLock()
        {
            if (!IsTradingLocked())
                return false;

            var reason = _maxLossLockTriggered ? "MAX_LOSS" : "MAX_DAILY_LOSS";
            if (_lastRiskLockReason != reason)
            {
                _lastRiskLockReason = reason;
                Print("FTMO risk lock triggered: {0}. Equity: {1}", reason, Account.Equity);
            }

            CloseBotPositions();
            return true;
        }

        private bool IsTradingLocked()
        {
            return _dailyLossLockTriggered || _maxLossLockTriggered;
        }

        private void CloseBotPositions()
        {
            foreach (var position in Positions)
            {
                if (position.SymbolName != SymbolName || position.Label != BotLabel)
                    continue;

                var result = ClosePosition(position);
                if (!result.IsSuccessful)
                    Print("Risk close failed for position {0}: {1}", position.Id, result.Error);
            }
        }

        private double ResolveFtmoMidnightBalance(DateTime ftmoDay)
        {
            var latestBalanceBeforeFtmoDay = double.NaN;
            var latestClosingTime = DateTime.MinValue;

            foreach (var trade in History)
            {
                if (trade.ClosingTime > ftmoDay || trade.ClosingTime < latestClosingTime)
                    continue;

                latestClosingTime = trade.ClosingTime;
                latestBalanceBeforeFtmoDay = trade.Balance;
            }

            if (!double.IsNaN(latestBalanceBeforeFtmoDay))
                return latestBalanceBeforeFtmoDay;

            return _initialBalance;
        }

        private double GetNearest8020Level(double price)
        {
            var baseLevel = Math.Floor(price / 100.0) * 100.0;
            var candidates = new List<double>
            {
                baseLevel - 20.0,
                baseLevel + 20.0,
                baseLevel + 80.0,
                baseLevel + 120.0
            };

            var nearest = candidates[0];
            var minDistance = Math.Abs(price - nearest);

            for (var i = 1; i < candidates.Count; i++)
            {
                var distance = Math.Abs(price - candidates[i]);
                if (distance < minDistance)
                {
                    minDistance = distance;
                    nearest = candidates[i];
                }
            }

            return nearest;
        }

        private sealed class SyntheticBar
        {
            public SyntheticBar(DateTime openTime, double price)
            {
                OpenTime = openTime;
                Open = price;
                High = price;
                Low = price;
                Close = price;
            }

            public DateTime OpenTime { get; }
            public double Open { get; private set; }
            public double High { get; private set; }
            public double Low { get; private set; }
            public double Close { get; private set; }
            public double Range => Math.Max(High - Low, 0.0000001);

            public void Update(double price)
            {
                High = Math.Max(High, price);
                Low = Math.Min(Low, price);
                Close = price;
            }
        }
    }
}
