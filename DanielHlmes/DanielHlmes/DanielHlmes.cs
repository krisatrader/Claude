using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum StopLossType
    {
        BreakoutBar,
        RangeOpposite,
        FixedPips,
        AtrBased
    }

    [Robot(TimeZone = TimeZones.CentralEuropeanStandardTime, AccessRights = AccessRights.None)]
    public class DanielHlmes : Robot
    {
        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — Setup Selection
        // ══════════════════════════════════════════════════════════════
        [Parameter("Enable Setup A (Breakout)", Group = "Setup Selection", DefaultValue = true)]
        public bool EnableSetupA { get; set; }

        [Parameter("Enable Setup B (Pullback)", Group = "Setup Selection", DefaultValue = false)]
        public bool EnableSetupB { get; set; }


        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — Range Setup (Support & Resistance)
        // ══════════════════════════════════════════════════════════════
        [Parameter("Start Hour (CET)", Group = "Session Range Setup", DefaultValue = 15, MinValue = 0, MaxValue = 23)]
        public int SessionStartHour { get; set; }

        [Parameter("Start Minute (CET)", Group = "Session Range Setup", DefaultValue = 0, MinValue = 0, MaxValue = 59)]
        public int SessionStartMinute { get; set; }

        [Parameter("End Hour (CET)", Group = "Session Range Setup", DefaultValue = 22, MinValue = 0, MaxValue = 23)]
        public int SessionEndHour { get; set; }

        [Parameter("End Minute (CET)", Group = "Session Range Setup", DefaultValue = 0, MinValue = 0, MaxValue = 59)]
        public int SessionEndMinute { get; set; }

        [Parameter("Range Lookback Bars", Group = "Session Range Setup", DefaultValue = 40, MinValue = 2, MaxValue = 100)]
        public int RangeLookbackBars { get; set; }


        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — Breakout Filters
        // ══════════════════════════════════════════════════════════════
        [Parameter("Doji Body Threshold Ratio", Group = "Breakout Filters", DefaultValue = 0.40, MinValue = 0.05, MaxValue = 0.95)]
        public double DojiThreshold { get; set; }

        [Parameter("No-Wick Threshold (Pips)", Group = "Breakout Filters", DefaultValue = 0.5, MinValue = 0.0, MaxValue = 10.0)]
        public double NoWickThreshold { get; set; }


        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — Entry Settings
        // ══════════════════════════════════════════════════════════════
        [Parameter("Entry Buffer (Pips)", Group = "Entry Settings", DefaultValue = 1.0, MinValue = 0.0, MaxValue = 20.0)]
        public double EntryBufferPips { get; set; }

        [Parameter("Stop Order Expiry (Bars)", Group = "Entry Settings", DefaultValue = 20, MinValue = 1, MaxValue = 100)]
        public int LimitOrderExpiryBars { get; set; }


        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — Setup A (Breakout + Retest)
        // ══════════════════════════════════════════════════════════════
        [Parameter("Stop Loss Type (Setup A)", Group = "Setup A", DefaultValue = StopLossType.BreakoutBar)]
        public StopLossType SlType { get; set; }

        [Parameter("Fixed SL Pips (Setup A)", Group = "Setup A", DefaultValue = 30.0, MinValue = 1.0, MaxValue = 500.0)]
        public double FixedSlPips { get; set; }

        [Parameter("ATR SL Multiplier (Setup A)", Group = "Setup A", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 10.0)]
        public double AtrSlMultiplier { get; set; }

        [Parameter("Reward-to-Risk Ratio (Setup A TP)", Group = "Setup A", DefaultValue = 5.0, MinValue = 0.5, MaxValue = 10.0)]
        public double RewardRiskRatio { get; set; }

        [Parameter("Max SL Pips (Setup A, 0=Off)", Group = "Setup A", DefaultValue = 30.0, MinValue = 0.0, MaxValue = 500.0)]
        public double SetupAMaxSlPips { get; set; }

        [Parameter("Order Expiry Hours", Group = "Setup A", DefaultValue = 8, MinValue = 1, MaxValue = 96)]
        public int LimitOrderExpiryHours { get; set; }


        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — Setup B (Pullback Continuation)
        // ══════════════════════════════════════════════════════════════
        [Parameter("Trend EMA Period", Group = "Setup B", DefaultValue = 200, MinValue = 5, MaxValue = 500)]
        public int TrendEmaPeriod { get; set; }

        [Parameter("Setup B Final TP Ratio", Group = "Setup B", DefaultValue = 3.0, MinValue = 0.5, MaxValue = 10.0)]
        public double SetupBFinalTpRatio { get; set; }

        [Parameter("Max SL Pips (Setup B, 0=Off)", Group = "Setup B", DefaultValue = 30.0, MinValue = 0.0, MaxValue = 500.0)]
        public double SetupBMaxSlPips { get; set; }

        [Parameter("Setup B Entry Window Start Hour (CET)", Group = "Setup B", DefaultValue = 2, MinValue = 0, MaxValue = 23)]
        public int SetupBEntryStartHour { get; set; }

        [Parameter("Setup B Entry Window End Hour (CET)", Group = "Setup B", DefaultValue = 16, MinValue = 0, MaxValue = 23)]
        public int SetupBEntryEndHour { get; set; }

        [Parameter("Min Range Width (Pips)", Group = "Setup B", DefaultValue = 40.0, MinValue = 0.0, MaxValue = 500.0)]
        public double MinRangeWidthPips { get; set; }

        [Parameter("Min Engulfing Body (Pips)", Group = "Setup B", DefaultValue = 5.0, MinValue = 0.0, MaxValue = 100.0)]
        public double MinEngulfingBodyPips { get; set; }

        [Parameter("Max EMA Distance (ATR multiplier, 0=Off)", Group = "Setup B", DefaultValue = 3.0, MinValue = 0.0, MaxValue = 20.0)]
        public double MaxEmaDistanceAtr { get; set; }


        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — Position Sizing & Exits
        // ══════════════════════════════════════════════════════════════
        [Parameter("Risk % per Trade", Group = "Position & Exits", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 10.0)]
        public double RiskPercent { get; set; }

        [Parameter("Enable Partial Close (Setup B)", Group = "Position & Exits", DefaultValue = false)]
        public bool EnablePartialClose { get; set; }

        [Parameter("Partial Close R-Trigger", Group = "Position & Exits", DefaultValue = 1.5, MinValue = 0.1, MaxValue = 10.0)]
        public double PartialCloseRTrigger { get; set; }

        [Parameter("Partial Close %", Group = "Position & Exits", DefaultValue = 50, MinValue = 10, MaxValue = 90)]
        public int PartialClosePct { get; set; }

        [Parameter("Enable Break-Even (Setup B)", Group = "Position & Exits", DefaultValue = true)]
        public bool EnableBreakEven { get; set; }

        [Parameter("Break-Even R-Multiple", Group = "Position & Exits", DefaultValue = 1.5, MinValue = 0.1, MaxValue = 10.0)]
        public double BreakEvenRMultiple { get; set; }

        [Parameter("Trailing ATR Multiplier (0=Disable)", Group = "Position & Exits", DefaultValue = 2.0, MinValue = 0.0, MaxValue = 10.0)]
        public double TrailingAtrMultiplier { get; set; }

        [Parameter("Extra SL Buffer (Pips)", Group = "Position & Exits", DefaultValue = 2.0, MinValue = 0.0, MaxValue = 50.0)]
        public double ExtraSlBufferPips { get; set; }

        [Parameter("ATR Period", Group = "Position & Exits", DefaultValue = 14, MinValue = 2, MaxValue = 100)]
        public int AtrPeriod { get; set; }


        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — Risk Filters & FTMO
        // ══════════════════════════════════════════════════════════════
        [Parameter("Use EMA Trend Filter for Breakouts", Group = "Risk Filters & FTMO", DefaultValue = true)]
        public bool UseEmaFilter { get; set; }

        [Parameter("Use H4 EMA Trend Filter for Breakouts", Group = "Risk Filters & FTMO", DefaultValue = true)]
        public bool UseH4EmaFilter { get; set; }

        [Parameter("Challenge Start Balance", Group = "Risk Filters & FTMO", DefaultValue = 25000.0, MinValue = 0.0, MaxValue = 2000000.0)]
        public double ChallengeStartBalance { get; set; }

        [Parameter("Max Daily DD %", Group = "Risk Filters & FTMO", DefaultValue = 5.0, MinValue = 0.5, MaxValue = 10.0)]
        public double MaxDailyDrawdownPct { get; set; }

        [Parameter("Max Total DD %", Group = "Risk Filters & FTMO", DefaultValue = 10.0, MinValue = 1.0, MaxValue = 20.0)]
        public double MaxTotalDrawdownPct { get; set; }

        [Parameter("Max Consecutive Losses (0=Off)", Group = "Risk Filters & FTMO", DefaultValue = 5, MinValue = 0, MaxValue = 20)]
        public int MaxConsecutiveLosses { get; set; }

        [Parameter("Cooldown Days After Streak", Group = "Risk Filters & FTMO", DefaultValue = 5, MinValue = 0, MaxValue = 14)]
        public int CooldownDays { get; set; }

        [Parameter("Max Weekly Loss % (0=Off)", Group = "Risk Filters & FTMO", DefaultValue = 3.0, MinValue = 0.0, MaxValue = 20.0)]
        public double MaxWeeklyLossPercent { get; set; }


        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — Setup C (Breakout Fade)
        // ══════════════════════════════════════════════════════════════
        [Parameter("Enable Setup C (Fade)", Group = "Setup Selection", DefaultValue = true)]
        public bool EnableSetupC { get; set; }

        [Parameter("Setup C Fade Buffer (Pips)", Group = "Setup C", DefaultValue = 0.0, MinValue = 0.0, MaxValue = 10.0)]
        public double SetupCFadeBufferPips { get; set; }

        [Parameter("Setup C Min Reward-Risk Ratio", Group = "Setup C", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 10.0)]
        public double SetupCRewardRiskRatio { get; set; }

        [Parameter("Max SL Pips (Setup C, 0=Off)", Group = "Setup C", DefaultValue = 30.0, MinValue = 0.0, MaxValue = 100.0)]
        public double SetupCMaxSlPips { get; set; }


        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — Log & Label Control
        // ══════════════════════════════════════════════════════════════
        [Parameter("Base Label", Group = "Log & Label Control", DefaultValue = "DanielHolmes")]
        public string Label { get; set; }

        [Parameter("Enable Logger", Group = "Log & Label Control", DefaultValue = true)]
        public bool EnableLogger { get; set; }

        [Parameter("Enable Candle-Close Invalidation (Setup B)", Group = "Log & Label Control", DefaultValue = true)]
        public bool EnableCandleCloseInvalidation { get; set; }

        [Parameter("Invalidation Buffer (Pips)", Group = "Log & Label Control", DefaultValue = 3.0, MinValue = 0.0, MaxValue = 50.0)]
        public double InvalidationBufferPips { get; set; }


        // ══════════════════════════════════════════════════════════════
        // PRIVATE FIELDS
        // ══════════════════════════════════════════════════════════════
        private ExponentialMovingAverage _trendEma;
        private AverageTrueRange _atr;
        private Bars _h4Bars;
        private ExponentialMovingAverage _h4Ema;

        private string LabelA => Label + "_A";
        private string LabelB => Label + "_B";

        private double _rangeHigh = double.MinValue;
        private double _rangeLow = double.MaxValue;
        private bool _rangeLocked = false;
        private DateTime _currentSessionStart = DateTime.MinValue;
        private DateTime _currentSessionEnd = DateTime.MinValue;

        private bool _tradedToday = false;
        private bool _breakoutDetected = false;
        private int _breakoutDirection = 0; // 0=None, 1=Up, -1=Down
        private int _breakoutBarIndex = -1;

        private bool _limitOrderPlaced = false;
        private bool _limitOrderActive = false;
        private int _limitOrderBarsRemaining = 0;
        private DateTime _limitOrderPlacedTime = DateTime.MinValue; // time-based expiry
        private PendingOrder _pendingOrder = null;

        private double _initialBalance;
        private double _challengeStartBal;
        private double _dailyStartValue;
        private double _peakBalance;
        private DateTime _lastDayChecked;
        private DateTime _botStartTime;

        // Profit-protection state
        private int      _consecutiveLosses = 0;
        private DateTime _cooldownUntil     = DateTime.MinValue;
        private double   _weekStartValue    = 0;
        private double   _weekPeakValue     = 0;  // highest equity seen this week
        private DateTime _weekStartDate     = DateTime.MinValue;

        // Trade State (Setup B)
        private double _initialSlPips = 0;
        private bool _breakEvenSet = false;
        private bool _partialCloseDone = false;
        private double _entryPrice = 0;
        private double _entryRiskAmount = 0;
        private int _logTradeSeq = 0;
        private int _entryBarIndex = -1; // Bar index when the Setup B position was opened

        // Trade State (Setup C)
        private bool _breakoutDetectedC = false;
        private int _breakoutDirectionC = 0; // 1 = bullish, -1 = bearish
        private int _breakoutBarIndexC = -1;
        private double _fadeExtremePrice = 0;
        private string LabelC => Label + "_C";

        private const string Sep = "══════════════════════════════════════════════════════════════";

        // ══════════════════════════════════════════════════════════════
        // LIFECYCLE EVENTS
        // ══════════════════════════════════════════════════════════════
        protected override void OnStart()
        {
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Simple);
            _trendEma = Indicators.ExponentialMovingAverage(Bars.ClosePrices, TrendEmaPeriod);
            _h4Bars = MarketData.GetBars(TimeFrame.Hour4);
            _h4Ema = Indicators.ExponentialMovingAverage(_h4Bars.ClosePrices, 200);

            _initialBalance = Account.Balance;
            _challengeStartBal = ChallengeStartBalance > 0 ? ChallengeStartBalance : Account.Balance;
            _peakBalance = Account.Balance;
            _dailyStartValue = Math.Max(Account.Balance, Account.Equity);
            _lastDayChecked = Server.Time.Date;
            _botStartTime = Server.Time;

            // Initialise week-tracking for weekly loss limit
            _weekStartDate  = GetWeekMonday(Server.Time.Date);
            _weekStartValue = Account.Equity;
            _weekPeakValue  = Account.Equity;

            Print($"[Start] Daniel Holmes Strategy cBot started.");
            Print($"[Start] Balance={_initialBalance:F2} | Challenge Balance={_challengeStartBal:F2}");
            Print($"[Start] Range Session CET: {SessionStartHour:D2}:{SessionStartMinute:D2} to {SessionEndHour:D2}:{SessionEndMinute:D2}");

            Positions.Opened += OnPositionsOpened;
            Positions.Closed += OnPositionClosed;
        }

        protected override void OnBar()
        {
            ResetWeeklyState();  // must come first — uses Server.Time
            ResetDailyState();
            UpdatePeakBalance();
            BuildSessionRange();

            if (_limitOrderActive)
            {
                ManagePendingOrderExpiry();
            }

            // Candle-close invalidation for open Setup B positions
            if (EnableCandleCloseInvalidation)
            {
                CheckCandleCloseInvalidation();
            }

            // Evaluate Setup A (Consolidation Breakout)
            if (_rangeLocked && !_tradedToday && !_breakoutDetected)
            {
                EvaluateBreakout();
            }

            // Evaluate Setup B (Pullback Continuation)
            if (!_tradedToday && !_breakoutDetected)
            {
                EvaluateSetupB();
            }

            // Evaluate Setup C (Breakout Fade)
            if (EnableSetupC && _breakoutDetectedC)
            {
                UpdateFadeExtremePrice();
                CheckSetupCFade();
            }
        }

        protected override void OnTick()
        {
            // 1. Always check total drawdown limit (hard cap)
            CheckDrawdownLimits();

            // 2. Invalidation Check for Pending Orders
            if (_limitOrderActive && _pendingOrder != null)
            {
                CheckPendingOrderInvalidation();
            }

            // 3. Manage running positions
            ManageOpenPositions();
            ManageTrailingStop();
        }

        protected override void OnStop()
        {
            if (EnableLogger)
            {
                LogShutdownSummary();
            }
        }

        // ══════════════════════════════════════════════════════════════
        // RANGE BUILDER (CANDLE BODIES)
        // ══════════════════════════════════════════════════════════════
        private void BuildSessionRange()
        {
            if (Bars.Count < 2) return;

            DateTime candleOpenTime = Bars.OpenTimes.Last(1);
            bool inSession = IsTimeInSession(candleOpenTime, out DateTime sessionStart, out DateTime sessionEnd);

            if (inSession)
            {
                if (sessionStart != _currentSessionStart)
                {
                    // Cancel any live pending order from the PREVIOUS session before resetting flags.
                    // Without this, the old order stays on the market for days and can fill with
                    // a completely wrong SL distance once price has drifted far away.
                    if (_pendingOrder != null)
                    {
                        Print($"[Session] New session — cancelling leftover pending order from previous session.");
                        CancelPendingOrder(_pendingOrder);
                        _pendingOrder = null;
                    }

                    // Reset range values at start of new session
                    _currentSessionStart = sessionStart;
                    _currentSessionEnd = sessionEnd;
                    _rangeHigh = double.MinValue;
                    _rangeLow = double.MaxValue;
                    _rangeLocked = false;
                    _tradedToday = false;
                    _breakoutDetected = false;
                    _breakoutDirection = 0;
                    _limitOrderPlaced = false;
                    _limitOrderActive = false;
                    ResetSetupCState();

                    Print($"[Session] New session started. Range Window: {sessionStart:yyyy-MM-dd HH:mm} to {sessionEnd:yyyy-MM-dd HH:mm} CET/CEST");
                }

                // Update range using candle body highs and lows
                double bodyHigh = Math.Max(Bars.OpenPrices.Last(1), Bars.ClosePrices.Last(1));
                double bodyLow = Math.Min(Bars.OpenPrices.Last(1), Bars.ClosePrices.Last(1));

                _rangeHigh = Math.Max(_rangeHigh, bodyHigh);
                _rangeLow = Math.Min(_rangeLow, bodyLow);
            }
            else
            {
                // Lock range when session ends
                if (_currentSessionStart != DateTime.MinValue && !_rangeLocked)
                {
                    _rangeLocked = true;
                    Print($"[Session] Range locked (candle bodies). High={_rangeHigh:F5}, Low={_rangeLow:F5}, Width={((_rangeHigh - _rangeLow) / Symbol.PipSize):F1} pips");
                }
            }
        }

        private bool IsTimeInSession(DateTime time, out DateTime sessionStart, out DateTime sessionEnd)
        {
            DateTime date = time.Date;
            TimeSpan startOpt = new TimeSpan(SessionStartHour, SessionStartMinute, 0);
            TimeSpan endOpt = new TimeSpan(SessionEndHour, SessionEndMinute, 0);

            if (startOpt < endOpt)
            {
                sessionStart = date.Add(startOpt);
                sessionEnd = date.Add(endOpt);
                return time >= sessionStart && time < sessionEnd;
            }
            else
            {
                DateTime startToday = date.Add(startOpt);
                DateTime endToday = date.Add(endOpt);

                if (time.TimeOfDay >= startOpt)
                {
                    sessionStart = startToday;
                    sessionEnd = endToday.AddDays(1);
                    return true;
                }
                else if (time.TimeOfDay < endOpt)
                {
                    sessionStart = startToday.AddDays(-1);
                    sessionEnd = endToday;
                    return true;
                }
                else
                {
                    sessionStart = DateTime.MinValue;
                    sessionEnd = DateTime.MinValue;
                    return false;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        // SETUP A: BREAKOUT EVALUATION & ORDER PLACEMENT
        // ══════════════════════════════════════════════════════════════
        private void EvaluateBreakout()
        {
            if (!EnableSetupA) return;
            if (!IsEntryAllowed()) return;

            double lastClose = Bars.ClosePrices.Last(1);
            double lastOpen = Bars.OpenPrices.Last(1);
            double lastHigh = Bars.HighPrices.Last(1);
            double lastLow = Bars.LowPrices.Last(1);

            bool emaLongOk = true;
            bool emaShortOk = true;
            if (UseEmaFilter)
            {
                double emaVal = _trendEma.Result.Last(1);
                emaLongOk = lastClose > emaVal;
                emaShortOk = lastClose < emaVal;
            }

            bool h4LongOk = true;
            bool h4ShortOk = true;
            if (UseH4EmaFilter)
            {
                double h4EmaVal = _h4Ema.Result.Last(1);
                h4LongOk = lastClose > h4EmaVal;
                h4ShortOk = lastClose < h4EmaVal;
            }

            if (lastClose > _rangeHigh && emaLongOk && h4LongOk)
            {
                // Doji Filter
                double barRange = lastHigh - lastLow;
                double bodySize = Math.Abs(lastClose - lastOpen);
                if (barRange > 0 && (bodySize / barRange) < DojiThreshold)
                {
                    Print($"[Filters] Setup A breakout candle at {lastClose:F5} is Doji (Ratio={(bodySize / barRange):F2} < {DojiThreshold}) — skip");
                    return;
                }

                // No-Wick Filter
                double topWick = lastHigh - Math.Max(lastOpen, lastClose);
                if (topWick <= NoWickThreshold * Symbol.PipSize)
                {
                    Print($"[Filters] Setup A breakout candle has no top wick (Wick={topWick/Symbol.PipSize:F2}p <= {NoWickThreshold}p) — skip due to wick fill risk");
                    return;
                }

                _breakoutDetected = true;
                _breakoutDirection = 1;
                _breakoutBarIndex = Bars.Count - 2;
                Print($"[Breakout] Setup A Bullish breakout confirmed at {lastClose:F5}. S/R (Body High): {_rangeHigh:F5}");
                
                if (EnableSetupC)
                {
                    _breakoutDetectedC = true;
                    _breakoutDirectionC = 1;
                    _breakoutBarIndexC = Bars.Count - 2;
                    _fadeExtremePrice = lastHigh;
                    Print($"[Setup C] Bullish Breakout Fade monitoring started. Extreme Price={_fadeExtremePrice:F5}");
                }

                PlaceSetupAStopOrder();
            }
            else if (lastClose < _rangeLow && emaShortOk && h4ShortOk)
            {
                // Doji Filter
                double barRange = lastHigh - lastLow;
                double bodySize = Math.Abs(lastClose - lastOpen);
                if (barRange > 0 && (bodySize / barRange) < DojiThreshold)
                {
                    Print($"[Filters] Setup A bearish breakout candle at {lastClose:F5} is Doji (Ratio={(bodySize / barRange):F2} < {DojiThreshold}) — skip");
                    return;
                }

                // No-Wick Filter
                double bottomWick = Math.Min(lastOpen, lastClose) - lastLow;
                if (bottomWick <= NoWickThreshold * Symbol.PipSize)
                {
                    Print($"[Filters] Setup A bearish breakout candle has no bottom wick (Wick={bottomWick/Symbol.PipSize:F2}p <= {NoWickThreshold}p) — skip due to wick fill risk");
                    return;
                }

                _breakoutDetected = true;
                _breakoutDirection = -1;
                _breakoutBarIndex = Bars.Count - 2;
                Print($"[Breakout] Setup A Bearish breakout confirmed at {lastClose:F5}. S/R (Body Low): {_rangeLow:F5}");
                
                if (EnableSetupC)
                {
                    _breakoutDetectedC = true;
                    _breakoutDirectionC = -1;
                    _breakoutBarIndexC = Bars.Count - 2;
                    _fadeExtremePrice = lastLow;
                    Print($"[Setup C] Bearish Breakout Fade monitoring started. Extreme Price={_fadeExtremePrice:F5}");
                }

                PlaceSetupAStopOrder();
            }
        }

        private void PlaceSetupAStopOrder()
        {
            if (_limitOrderPlaced || _tradedToday) return;

            TradeType direction = _breakoutDirection == 1 ? TradeType.Buy : TradeType.Sell;

            // Place pending Stop Order at the breakout candle's extreme
            double targetPrice = _breakoutDirection == 1
                ? Bars.HighPrices.Last(1) + EntryBufferPips * Symbol.PipSize
                : Bars.LowPrices.Last(1) - EntryBufferPips * Symbol.PipSize;

            double stopLossPips = CalculateStopLossPips(direction, false);
            double takeProfitPips = stopLossPips * RewardRiskRatio; // Fix 1:1 TP
            double volume = CalculateVolume(stopLossPips);

            if (volume <= 0) return;

            // Max SL guard — reject if the stop distance is unreasonably wide
            if (SetupAMaxSlPips > 0 && stopLossPips > SetupAMaxSlPips)
            {
                Print($"[Exec Setup A] Skip: SL too wide ({stopLossPips:F1}p > max {SetupAMaxSlPips}p)");
                return;
            }

            if (direction == TradeType.Buy && targetPrice <= Symbol.Ask)
            {
                Print($"[Exec Setup A] Skip placing Buy Stop order. Target price {targetPrice:F5} is already <= Ask {Symbol.Ask:F5}");
                return;
            }
            if (direction == TradeType.Sell && targetPrice >= Symbol.Bid)
            {
                Print($"[Exec Setup A] Skip placing Sell Stop order. Target price {targetPrice:F5} is already >= Bid {Symbol.Bid:F5}");
                return;
            }

            double slPrice = direction == TradeType.Buy
                ? targetPrice - stopLossPips * Symbol.PipSize
                : targetPrice + stopLossPips * Symbol.PipSize;
            double tpPrice = direction == TradeType.Buy
                ? targetPrice + takeProfitPips * Symbol.PipSize
                : targetPrice - takeProfitPips * Symbol.PipSize;

            var result = PlaceStopOrder(direction, SymbolName, volume, targetPrice, LabelA, slPrice, tpPrice, ProtectionType.Absolute, null, null, false, StopTriggerMethod.Trade);
            if (result.IsSuccessful)
            {
                _limitOrderPlaced = true;
                _limitOrderActive = true;
                _limitOrderBarsRemaining = LimitOrderExpiryBars;
                _limitOrderPlacedTime = Server.Time;
                _pendingOrder = result.PendingOrder;
                Print($"[Exec Setup A] Placed Stop {direction} @ {targetPrice:F5} | SLPrice={slPrice:F5} ({stopLossPips:F1}p) | TPPrice={tpPrice:F5} ({takeProfitPips:F1}p) | Vol={volume}");
            }
            else
            {
                Print($"[Exec Setup A] Stop order placement failed: {result.Error}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        // SETUP B: PULLBACK CONTINUATION
        // ══════════════════════════════════════════════════════════════
        private void EvaluateSetupB()
        {
            if (!EnableSetupB || _tradedToday) return;
            if (!IsEntryAllowed()) return;
            if (Bars.Count < 3) return;

            // ── FILTER 1: Entry time window (avoids illiquid night hours) ──
            int currentHour = Server.Time.Hour;
            bool inEntryWindow;
            if (SetupBEntryEndHour > SetupBEntryStartHour)
                inEntryWindow = currentHour >= SetupBEntryStartHour && currentHour < SetupBEntryEndHour;
            else
                inEntryWindow = currentHour >= SetupBEntryStartHour || currentHour < SetupBEntryEndHour;

            if (!inEntryWindow)
                return; // Outside liquid trading hours — skip Setup B

            // ── FILTER 2: Minimum range width (avoids fake/narrow ranges) ──
            if (_rangeLocked && MinRangeWidthPips > 0)
            {
                double rangeWidth = (_rangeHigh - _rangeLow) / Symbol.PipSize;
                if (rangeWidth < MinRangeWidthPips)
                {
                    return; // Range too narrow to be significant S&R
                }
            }

            double emaVal = _trendEma.Result.Last(1);
            double lastClose = Bars.ClosePrices.Last(1);
            bool isBullishTrend = lastClose > emaVal;
            bool isBearishTrend = lastClose < emaVal;

            // ── FILTER 3: EMA proximity — engulfing must be within MaxEmaDistanceAtr×ATR of the EMA ──
            if (MaxEmaDistanceAtr > 0)
            {
                double atrVal = _atr.Result.Last(1);
                double distToEma = Math.Abs(lastClose - emaVal);
                if (distToEma > MaxEmaDistanceAtr * atrVal)
                {
                    return; // Pullback too far from EMA — likely overextended, not a valid setup
                }
            }

            bool isBullishEngulfing = false;
            bool isBearishEngulfing = false;

            if (isBullishTrend)
            {
                // Bullish trend: pullback was bearish, engulfing candle is bullish
                bool bar2IsBearish = Bars.ClosePrices.Last(2) < Bars.OpenPrices.Last(2);
                bool bar1IsBullish = Bars.ClosePrices.Last(1) > Bars.OpenPrices.Last(1);

                double body2 = Math.Abs(Bars.ClosePrices.Last(2) - Bars.OpenPrices.Last(2));
                double body1 = Math.Abs(Bars.ClosePrices.Last(1) - Bars.OpenPrices.Last(1));

                // ── FILTER 4: Minimum engulfing body size ──
                if (bar2IsBearish && bar1IsBullish && body1 > body2 &&
                    Bars.OpenPrices.Last(1) <= Bars.ClosePrices.Last(2) &&
                    Bars.ClosePrices.Last(1) >= Bars.OpenPrices.Last(2))
                {
                    if (MinEngulfingBodyPips > 0 && body1 / Symbol.PipSize < MinEngulfingBodyPips)
                    {
                        Print($"[Setup B Filter] Bullish engulfing body too small: {body1 / Symbol.PipSize:F1}p < {MinEngulfingBodyPips}p — skip");
                    }
                    else
                    {
                        isBullishEngulfing = true;
                    }
                }
            }
            else if (isBearishTrend)
            {
                // Bearish trend: pullback was bullish, engulfing candle is bearish
                bool bar2IsBullish = Bars.ClosePrices.Last(2) > Bars.OpenPrices.Last(2);
                bool bar1IsBearish = Bars.ClosePrices.Last(1) < Bars.OpenPrices.Last(1);

                double body2 = Math.Abs(Bars.ClosePrices.Last(2) - Bars.OpenPrices.Last(2));
                double body1 = Math.Abs(Bars.ClosePrices.Last(1) - Bars.OpenPrices.Last(1));

                // ── FILTER 4: Minimum engulfing body size ──
                if (bar2IsBullish && bar1IsBearish && body1 > body2 &&
                    Bars.OpenPrices.Last(1) >= Bars.ClosePrices.Last(2) &&
                    Bars.ClosePrices.Last(1) <= Bars.OpenPrices.Last(2))
                {
                    if (MinEngulfingBodyPips > 0 && body1 / Symbol.PipSize < MinEngulfingBodyPips)
                    {
                        Print($"[Setup B Filter] Bearish engulfing body too small: {body1 / Symbol.PipSize:F1}p < {MinEngulfingBodyPips}p — skip");
                    }
                    else
                    {
                        isBearishEngulfing = true;
                    }
                }
            }

            if (isBullishEngulfing)
            {
                _breakoutDetected = true;
                _breakoutDirection = 1;
                _breakoutBarIndex = Bars.Count - 2;
                Print($"[Setup B] Bullish Engulfing confirmed at {lastClose:F5} | Hour={currentHour}:xx CET | EMA dist={(Math.Abs(lastClose - emaVal) / Symbol.PipSize):F1}p. Placing Buy Stop...");
                PlaceSetupBStopOrder(TradeType.Buy);
            }
            else if (isBearishEngulfing)
            {
                _breakoutDetected = true;
                _breakoutDirection = -1;
                _breakoutBarIndex = Bars.Count - 2;
                Print($"[Setup B] Bearish Engulfing confirmed at {lastClose:F5} | Hour={currentHour}:xx CET | EMA dist={(Math.Abs(lastClose - emaVal) / Symbol.PipSize):F1}p. Placing Sell Stop...");
                PlaceSetupBStopOrder(TradeType.Sell);
            }
        }

        private void PlaceSetupBStopOrder(TradeType direction)
        {
            if (_limitOrderPlaced || _tradedToday) return;

            double targetPrice = direction == TradeType.Buy
                ? Bars.HighPrices.Last(1) + EntryBufferPips * Symbol.PipSize
                : Bars.LowPrices.Last(1) - EntryBufferPips * Symbol.PipSize;

            double stopLossPips = CalculateStopLossPips(direction, true);
            double takeProfitPips = stopLossPips * SetupBFinalTpRatio;
            double volume = CalculateVolume(stopLossPips);

            if (volume <= 0) return;

            // Max SL guard — reject if the stop distance is unreasonably wide
            if (SetupBMaxSlPips > 0 && stopLossPips > SetupBMaxSlPips)
            {
                Print($"[Exec Setup B] Skip: SL too wide ({stopLossPips:F1}p > max {SetupBMaxSlPips}p)");
                return;
            }

            if (direction == TradeType.Buy && targetPrice <= Symbol.Ask)
            {
                Print($"[Exec Setup B] Skip placing Buy Stop order. Target price {targetPrice:F5} is already <= Ask {Symbol.Ask:F5}");
                return;
            }
            if (direction == TradeType.Sell && targetPrice >= Symbol.Bid)
            {
                Print($"[Exec Setup B] Skip placing Sell Stop order. Target price {targetPrice:F5} is already >= Bid {Symbol.Bid:F5}");
                return;
            }

            double slPrice = direction == TradeType.Buy
                ? targetPrice - stopLossPips * Symbol.PipSize
                : targetPrice + stopLossPips * Symbol.PipSize;
            double tpPrice = direction == TradeType.Buy
                ? targetPrice + takeProfitPips * Symbol.PipSize
                : targetPrice - takeProfitPips * Symbol.PipSize;

            var result = PlaceStopOrder(direction, SymbolName, volume, targetPrice, LabelB, slPrice, tpPrice, ProtectionType.Absolute, null, null, false, StopTriggerMethod.Trade);
            if (result.IsSuccessful)
            {
                _limitOrderPlaced = true;
                _limitOrderActive = true;
                _limitOrderBarsRemaining = LimitOrderExpiryBars;
                _limitOrderPlacedTime = Server.Time;
                _pendingOrder = result.PendingOrder;
                Print($"[Exec Setup B] Placed Stop {direction} @ {targetPrice:F5} | SLPrice={slPrice:F5} ({stopLossPips:F1}p) | TPPrice={tpPrice:F5} ({takeProfitPips:F1}p) | Vol={volume}");
            }
            else
            {
                Print($"[Exec Setup B] Stop order placement failed: {result.Error}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        // EXITS & POSITION MANAGEMENT
        // ══════════════════════════════════════════════════════════════
        private void ManageOpenPositions()
        {
            foreach (var pos in Positions)
            {
                if (pos.SymbolName != SymbolName) continue;

                if (pos.Label == LabelB)
                {
                    ManageSetupBPosition(pos);
                }
            }
        }

        private void ManageSetupBPosition(Position pos)
        {
            double currentPips = pos.TradeType == TradeType.Buy
                ? (Symbol.Bid - pos.EntryPrice) / Symbol.PipSize
                : (pos.EntryPrice - Symbol.Ask) / Symbol.PipSize;

            // STEP 1: Partial Close (80% profit taking)
            if (EnablePartialClose && !_partialCloseDone && currentPips >= _initialSlPips * PartialCloseRTrigger)
            {
                double closeVolume = Symbol.NormalizeVolumeInUnits(pos.VolumeInUnits * PartialClosePct / 100.0);
                if (closeVolume > 0 && closeVolume < pos.VolumeInUnits)
                {
                    var res = ClosePosition(pos, closeVolume);
                    if (res.IsSuccessful)
                    {
                        _partialCloseDone = true;
                        double actualR = _initialSlPips > 0 ? (currentPips / _initialSlPips) : 0;
                        Print($"[Setup B Exit] Partial Close: {PartialClosePct}% executed @ {actualR:F2}R");
                    }
                    else
                    {
                        Print($"[Setup B Exit] Partial Close failed: {res.Error}");
                    }
                }
            }

            // STEP 2: Break-Even
            if (EnableBreakEven && !_breakEvenSet)
            {
                bool triggerBe = false;
                if (_partialCloseDone)
                {
                    triggerBe = true; // Auto break-even after partial target hit
                }
                else if (currentPips >= _initialSlPips * BreakEvenRMultiple)
                {
                    triggerBe = true;
                }

                if (triggerBe)
                {
                    var res = ModifyPosition(pos, pos.EntryPrice, pos.TakeProfit, ProtectionType.Absolute);
                    if (res.IsSuccessful)
                    {
                        _breakEvenSet = true;
                        Print($"[Setup B Exit] Stop Loss moved to Break-Even @ {pos.EntryPrice:F5}");
                    }
                    else
                    {
                        Print($"[Setup B Exit] Failed to move Stop Loss to Break-Even: {res.Error}");
                    }
                }
            }
        }

        private void ManageTrailingStop()
        {
            if (TrailingAtrMultiplier <= 0) return;

            double atrValue = _atr.Result.Last(1);
            if (atrValue <= 0) return;

            double trailDistancePips = atrValue * TrailingAtrMultiplier / Symbol.PipSize;

            foreach (var pos in Positions)
            {
                if (pos.SymbolName != SymbolName || pos.Label != LabelB) continue;
                if (!_breakEvenSet) continue;

                if (pos.TradeType == TradeType.Buy)
                {
                    double newSl = Symbol.Bid - trailDistancePips * Symbol.PipSize;
                    if (pos.StopLoss == null || newSl > pos.StopLoss.Value)
                    {
                        ModifyPosition(pos, newSl, pos.TakeProfit, ProtectionType.Absolute);
                    }
                }
                else
                {
                    double newSl = Symbol.Ask + trailDistancePips * Symbol.PipSize;
                    if (pos.StopLoss == null || newSl < pos.StopLoss.Value)
                    {
                        ModifyPosition(pos, newSl, pos.TakeProfit, ProtectionType.Absolute);
                    }
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        // ORDER LIFECYCLE MANAGEMENT & FILTERS
        // ══════════════════════════════════════════════════════════════
        private void CheckPendingOrderInvalidation()
        {
            if (_pendingOrder == null) return;

            double lastClose = Bars.ClosePrices.Last(1);

            // Invalidation rules (price closes past opposite side of range or engulfing candle)
            bool isInvalid = false;
            if (_pendingOrder.TradeType == TradeType.Buy && lastClose < _rangeLow)
            {
                isInvalid = true;
                Print($"[Invalidation] Bullish Stop order cancelled: Close ({lastClose:F5}) fell below range support ({_rangeLow:F5})");
            }
            else if (_pendingOrder.TradeType == TradeType.Sell && lastClose > _rangeHigh)
            {
                isInvalid = true;
                Print($"[Invalidation] Bearish Stop order cancelled: Close ({lastClose:F5}) rose above range resistance ({_rangeHigh:F5})");
            }

            if (isInvalid)
            {
                CancelPendingOrder(_pendingOrder);
                _limitOrderActive = false;
                _limitOrderPlaced = false;
                _pendingOrder = null;
                _breakoutDetected = false;
            }
        }

        private void ManagePendingOrderExpiry()
        {
            if (!_limitOrderActive || _pendingOrder == null) return;

            // ── PRIMARY: time-based expiry ─────────────────────────────
            // Bar-count expiry fails over weekends (no bars, no countdown).
            // This time check cancels the order the moment the market
            // reopens after a holiday/weekend, BEFORE any gap fill occurs.
            if (_limitOrderPlacedTime != DateTime.MinValue &&
                (Server.Time - _limitOrderPlacedTime).TotalHours >= LimitOrderExpiryHours)
            {
                CancelPendingOrder(_pendingOrder);
                _limitOrderActive = false;
                _limitOrderPlaced = false;
                _pendingOrder = null;
                _limitOrderPlacedTime = DateTime.MinValue;
                _breakoutDetected = false;
                Print($"[Exec] Stop order expired (time-based, {LimitOrderExpiryHours}h) and cancelled.");
                return;
            }

            // ── SECONDARY: bar-count expiry (intra-week safety net) ────
            _limitOrderBarsRemaining--;
            if (_limitOrderBarsRemaining <= 0)
            {
                CancelPendingOrder(_pendingOrder);
                _limitOrderActive = false;
                _limitOrderPlaced = false;
                _pendingOrder = null;
                _limitOrderPlacedTime = DateTime.MinValue;
                _breakoutDetected = false;
                Print("[Exec] Stop order expired (bar-count) and cancelled.");
            }
        }

        private bool IsEntryAllowed()
        {
            // ── 1. FTMO Daily Drawdown ─────────────────────────────────────
            double dailyDD = (_dailyStartValue - Account.Equity) / _dailyStartValue * 100.0;
            if (dailyDD >= MaxDailyDrawdownPct)
            {
                Print($"[Risk] Daily DD Cap hit: {dailyDD:F2}% >= {MaxDailyDrawdownPct}% — blocked entries today.");
                return false;
            }

            // ── 2. FTMO Total Drawdown ─────────────────────────────────────
            double totalDD = (_challengeStartBal - Account.Equity) / _challengeStartBal * 100.0;
            if (totalDD >= MaxTotalDrawdownPct)
            {
                return false;
            }

            // ── 3. Consecutive-loss cooldown ───────────────────────────────
            // After N losses in a row the bot pauses CooldownDays to avoid
            // churning through unfavourable market conditions.
            if (_cooldownUntil > Server.Time)
            {
                Print($"[Risk] Cooldown aktív: {_consecutiveLosses} egymást követő veszteség. " +
                      $"Szünet: {_cooldownUntil:yyyy-MM-dd HH:mm} CET-ig.");
                return false;
            }

            // ── 4. Weekly loss limit ───────────────────────────────────────
            // Measured from the highest equity reached this week (peak),
            // so mid-week gains are also protected.
            if (MaxWeeklyLossPercent > 0 && _weekPeakValue > 0)
            {
                double weeklyLoss = (_weekPeakValue - Account.Equity) / _weekPeakValue * 100.0;
                if (weeklyLoss >= MaxWeeklyLossPercent)
                {
                    Print($"[Risk] Heti limit elérve: -{weeklyLoss:F2}% a heti csúcstól (${_weekPeakValue:F2}) — " +
                          $"hétre leállás.");
                    return false;
                }
            }

            // ── 5. Max 1 position at a time ───────────────────────────────
            if (Positions.FindAll(LabelA, SymbolName).Length > 0 || Positions.FindAll(LabelB, SymbolName).Length > 0)
            {
                return false;
            }

            return true;
        }

        // ── Weekly state reset ─────────────────────────────────────────────
        private void ResetWeeklyState()
        {
            DateTime monday = GetWeekMonday(Server.Time.Date);
            if (monday != _weekStartDate)
            {
                // New week: reset both start and peak
                _weekStartDate  = monday;
                _weekStartValue = Account.Equity;
                _weekPeakValue  = Account.Equity;
                Print($"[Weekly] Új hét ({monday:yyyy-MM-dd}): heti referencia equity = ${_weekStartValue:F2}");
            }
            else if (Account.Equity > _weekPeakValue)
            {
                // Same week: update peak if equity rose
                _weekPeakValue = Account.Equity;
            }
        }

        private static DateTime GetWeekMonday(DateTime date)
        {
            int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
            return date.AddDays(-diff);
        }


        private void CheckDrawdownLimits()
        {
            double totalDD = (_challengeStartBal - Account.Equity) / _challengeStartBal * 100.0;
            if (totalDD >= MaxTotalDrawdownPct)
            {
                Print($"[Risk] Total DD Cap hit: {totalDD:F2}% >= {MaxTotalDrawdownPct}% — STOPPING BOT.");
                CloseAllPositions("Total Drawdown limit hit");
                Stop();
            }
        }

        private void CloseAllPositions(string reason)
        {
            foreach (var pos in Positions)
            {
                if (pos.SymbolName != SymbolName) continue;
                if (pos.Label == LabelA || pos.Label == LabelB)
                {
                    ClosePosition(pos);
                    Print($"[Risk] Position closed: {pos.TradeType} | Reason: {reason}");
                }
            }
        }

        private double CalculateStopLossPips(TradeType direction, bool isSetupB)
        {
            double entryLevel = direction == TradeType.Buy
                ? Bars.HighPrices.Last(1) + EntryBufferPips * Symbol.PipSize
                : Bars.LowPrices.Last(1) - EntryBufferPips * Symbol.PipSize;

            double slPrice = 0;

            if (isSetupB)
            {
                // Setup B SL goes behind the engulfing candle opposite extreme
                if (direction == TradeType.Buy)
                    slPrice = Bars.LowPrices.Last(1) - ExtraSlBufferPips * Symbol.PipSize;
                else
                    slPrice = Bars.HighPrices.Last(1) + ExtraSlBufferPips * Symbol.PipSize;
            }
            else
            {
                switch (SlType)
                {
                    case StopLossType.BreakoutBar:
                        if (direction == TradeType.Buy)
                            slPrice = Bars.LowPrices.Last(1) - ExtraSlBufferPips * Symbol.PipSize;
                        else
                            slPrice = Bars.HighPrices.Last(1) + ExtraSlBufferPips * Symbol.PipSize;
                        break;

                    case StopLossType.RangeOpposite:
                        if (direction == TradeType.Buy)
                            slPrice = _rangeLow - ExtraSlBufferPips * Symbol.PipSize;
                        else
                            slPrice = _rangeHigh + ExtraSlBufferPips * Symbol.PipSize;
                        break;

                    case StopLossType.FixedPips:
                        return FixedSlPips;

                    case StopLossType.AtrBased:
                        double atr = _atr.Result.Last(1);
                        return atr * AtrSlMultiplier / Symbol.PipSize;
                }
            }

            double slPips = direction == TradeType.Buy
                ? (entryLevel - slPrice) / Symbol.PipSize
                : (slPrice - entryLevel) / Symbol.PipSize;

            return Math.Max(1.0, slPips);
        }

        private double CalculateVolume(double stopPips)
        {
            if (stopPips <= 0 || Symbol.PipValue <= 0) return 0;

            double riskAmount = Account.Balance * (RiskPercent / 100.0);
            double volume = riskAmount / (stopPips * Symbol.PipValue);
            volume = Symbol.NormalizeVolumeInUnits(volume);

            if (volume < Symbol.VolumeInUnitsMin) return 0;
            if (volume > Symbol.VolumeInUnitsMax) volume = Symbol.VolumeInUnitsMax;

            return volume;
        }

        private void ResetDailyState()
        {
            if (Server.Time.Date == _lastDayChecked) return;

            _dailyStartValue = Math.Max(Account.Balance, Account.Equity);
            _lastDayChecked = Server.Time.Date;
            _tradedToday = false;
            _breakEvenSet = false;
            _partialCloseDone = false;
            _limitOrderActive = false;
            ResetSetupCState();

            Print($"[Daily Reset] Date: {_lastDayChecked:yyyy-MM-dd} | Starting Value (High of Bal/Eq)={_dailyStartValue:F2}");
        }

        private void UpdatePeakBalance()
        {
            if (Account.Balance > _peakBalance)
            {
                _peakBalance = Account.Balance;
            }
        }

        // ══════════════════════════════════════════════════════════════
        // CALLBACK EVENTS
        // ══════════════════════════════════════════════════════════════
        private void OnPositionsOpened(PositionOpenedEventArgs args)
        {
            var pos = args.Position;
            if (pos.SymbolName != SymbolName) return;
            if (pos.Label != LabelA && pos.Label != LabelB && pos.Label != LabelC) return;

            _tradedToday = true;
            _limitOrderActive = false;
            _limitOrderPlaced = false;
            _pendingOrder = null;
            ResetSetupCState();

            bool isB = pos.Label == LabelB;
            bool isC = pos.Label == LabelC;
            _initialSlPips = pos.StopLoss != null
                ? Math.Abs(pos.EntryPrice - pos.StopLoss.Value) / Symbol.PipSize
                : CalculateStopLossPips(pos.TradeType, isB);

            // ── FILL-TIME SAFETY CHECK ────────────────────────────────────
            // A pending stop order can fill far from its original target price
            // (e.g. after price drifted significantly between sessions).
            // When that happens the fixed SL price creates a much wider actual
            // risk than the intended 1%.  Detect this and close immediately.
            double maxSlForSetup = isB ? SetupBMaxSlPips : (isC ? SetupCMaxSlPips : SetupAMaxSlPips);
            if (maxSlForSetup > 0 && _initialSlPips > maxSlForSetup)
            {
                double actualRiskPct = (_initialSlPips * pos.VolumeInUnits * Symbol.PipValue) / Account.Balance * 100.0;
                Print($"[Risk] Fill-time SL guard triggered! Actual SL = {_initialSlPips:F1}p > max {maxSlForSetup}p " +
                      $"(actual risk ~{actualRiskPct:F2}%). Closing position immediately.");
                ClosePosition(pos);
                return;
            }
            // ─────────────────────────────────────────────────────────────

            _breakEvenSet = false;
            _partialCloseDone = false;
            _entryPrice = pos.EntryPrice;
            _entryRiskAmount = Account.Balance * RiskPercent / 100.0;
            _entryBarIndex = Bars.Count - 1; // record which bar the position was opened on
            _logTradeSeq++;

            Print($"[Fill] Stop Order filled. Position opened: {pos.TradeType} | Label={pos.Label} | Entry={pos.EntryPrice:F5} | SL={_initialSlPips:F1}p");
            if (EnableLogger)
            {
                LogTradeOpen(pos, 0, Symbol.Spread / Symbol.PipSize);
            }
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var pos = args.Position;
            if (pos.SymbolName != SymbolName) return;
            if (pos.Label != LabelA && pos.Label != LabelB && pos.Label != LabelC) return;

            _entryBarIndex = -1; // reset on close

            // ── Consecutive-loss tracker ──────────────────────────────────
            bool isWin = pos.NetProfit > 0;
            if (isWin)
            {
                if (_consecutiveLosses > 0)
                    Print($"[Risk] Vesztő sorozat vége ({_consecutiveLosses} loss). Cooldown törölve.");
                _consecutiveLosses = 0;
                _cooldownUntil     = DateTime.MinValue; // reset cooldown on a win
            }
            else
            {
                _consecutiveLosses++;
                if (MaxConsecutiveLosses > 0 && _consecutiveLosses >= MaxConsecutiveLosses)
                {
                    _cooldownUntil = Server.Time.AddDays(CooldownDays);
                    Print($"[Risk] {_consecutiveLosses} egymást követő veszteség — " +
                          $"{CooldownDays} napos COOLDOWN aktiválva. Újraindulás: {_cooldownUntil:yyyy-MM-dd HH:mm}");
                }
                else
                {
                    Print($"[Risk] Vesztő sorozat: {_consecutiveLosses}/{MaxConsecutiveLosses}");
                }
            }

            double pips = (pos.VolumeInUnits > 0 && Symbol.PipValue > 0) ? pos.GrossProfit / (pos.VolumeInUnits * Symbol.PipValue) : 0;
            double closePrice = pos.TradeType == TradeType.Buy ? pos.EntryPrice + pips * Symbol.PipSize : pos.EntryPrice - pips * Symbol.PipSize;
            Print($"[Close/{args.Reason}] {pos.TradeType} @ {pos.EntryPrice:F5} | Closed @ {closePrice:F5} | Profit={pos.NetProfit:F2}");
            if (EnableLogger)
            {
                LogTradeClose(pos);
            }
        }

        // ══════════════════════════════════════════════════════════════
        // CANDLE-CLOSE INVALIDATION (Setup B)
        // If a full candle CLOSES beyond the entry price in the wrong
        // direction (+ buffer), the setup has failed → exit early.
        // ══════════════════════════════════════════════════════════════
        private void CheckCandleCloseInvalidation()
        {
            // Only acts after at least 1 full bar has closed since entry
            if (_entryBarIndex < 0 || Bars.Count - 1 <= _entryBarIndex) return;
            if (_breakEvenSet) return; // Already in profit territory — no need to invalidate

            double buffer = InvalidationBufferPips * Symbol.PipSize;

            foreach (var pos in Positions)
            {
                if (pos.SymbolName != SymbolName || pos.Label != LabelB) continue;

                double lastClose = Bars.ClosePrices.Last(1); // most recently closed candle

                bool invalidated = false;
                string reason = string.Empty;

                if (pos.TradeType == TradeType.Sell)
                {
                    // Sell is invalidated if a candle CLOSES above entry + buffer
                    if (lastClose > pos.EntryPrice + buffer)
                    {
                        invalidated = true;
                        reason = $"Bearish setup invalidated: candle closed at {lastClose:F5} > entry {pos.EntryPrice:F5} + {InvalidationBufferPips}p buffer";
                    }
                }
                else if (pos.TradeType == TradeType.Buy)
                {
                    // Buy is invalidated if a candle CLOSES below entry - buffer
                    if (lastClose < pos.EntryPrice - buffer)
                    {
                        invalidated = true;
                        reason = $"Bullish setup invalidated: candle closed at {lastClose:F5} < entry {pos.EntryPrice:F5} - {InvalidationBufferPips}p buffer";
                    }
                }

                if (invalidated)
                {
                    Print($"[Setup B Invalidation] {reason}. Closing position early.");
                    var res = ClosePosition(pos);
                    if (!res.IsSuccessful)
                        Print($"[Setup B Invalidation] Close failed: {res.Error}");
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        // LOGGER MODULE
        // ══════════════════════════════════════════════════════════════
        private void LogTradeOpen(Position pos, double slippagePips, double spreadAtFill)
        {
            Print(Sep);
            Print($"[TRADE OPEN #{_logTradeSeq}] ═ {pos.TradeType} | {pos.Label} ═");
            Print($"[TRADE OPEN #{_logTradeSeq}] Time       : {Server.Time:yyyy-MM-dd HH:mm:ss} CET/CEST");
            Print($"[TRADE OPEN #{_logTradeSeq}] Entry Price: {pos.EntryPrice:F5}");
            Print($"[TRADE OPEN #{_logTradeSeq}] Stop Loss : {pos.StopLoss:F5} ({_initialSlPips:F1}p)");
            Print($"[TRADE OPEN #{_logTradeSeq}] Volume    : {pos.VolumeInUnits}");
            Print($"[TRADE OPEN #{_logTradeSeq}] Spread    : {spreadAtFill:F1}p | Slippage: {slippagePips:F1}p");
            Print($"[TRADE OPEN #{_logTradeSeq}] Balance   : {Account.Balance:F2} | Daily Start Reference: {_dailyStartValue:F2}");
            Print(Sep);
        }

        private void LogTradeClose(Position pos)
        {
            double pipsResult = (pos.VolumeInUnits > 0 && Symbol.PipValue > 0)
                ? pos.GrossProfit / (pos.VolumeInUnits * Symbol.PipValue) : 0;
            double riskBase = _entryRiskAmount > 0 ? _entryRiskAmount : Account.Balance * RiskPercent / 100.0;
            double rMultiple = riskBase > 0 ? pos.NetProfit / riskBase : 0;
            bool isWin = pos.NetProfit > 0;

            Print(Sep);
            Print($"[TRADE CLOSE #{_logTradeSeq}] ═ {(isWin ? "WIN" : "LOSS")} | {pos.TradeType} | {pos.Label} ═");
            double closePrice = pos.TradeType == TradeType.Buy ? pos.EntryPrice + pipsResult * Symbol.PipSize : pos.EntryPrice - pipsResult * Symbol.PipSize;
            Print($"[TRADE CLOSE #{_logTradeSeq}] Time       : {Server.Time:yyyy-MM-dd HH:mm:ss} CET/CEST");
            Print($"[TRADE CLOSE #{_logTradeSeq}] Entry Price: {pos.EntryPrice:F5}");
            Print($"[TRADE CLOSE #{_logTradeSeq}] Close Price: {closePrice:F5}");
            Print($"[TRADE CLOSE #{_logTradeSeq}] Pips       : {pipsResult:F1} | R-Multiple: {rMultiple:F2}R");
            Print($"[TRADE CLOSE #{_logTradeSeq}] Net Profit: {pos.NetProfit:F2} {Account.Asset.Name}");
            Print($"[TRADE CLOSE #{_logTradeSeq}] Balance    : {Account.Balance:F2}");
            Print(Sep);
        }

        // ══════════════════════════════════════════════════════════════
        // SETUP C: BREAKOUT FADE HELPER METHODS
        // ══════════════════════════════════════════════════════════════
        private void UpdateFadeExtremePrice()
        {
            if (!_breakoutDetectedC) return;

            if (_breakoutDirectionC == 1)
            {
                _fadeExtremePrice = Math.Max(_fadeExtremePrice, Bars.HighPrices.Last(1));
            }
            else if (_breakoutDirectionC == -1)
            {
                _fadeExtremePrice = Math.Min(_fadeExtremePrice, Bars.LowPrices.Last(1));
            }
        }

        private void CheckSetupCFade()
        {
            if (!_breakoutDetectedC || _tradedToday) return;

            // Invalidation 1: Expiry in bars
            int barsPassed = Bars.Count - 1 - _breakoutBarIndexC;
            if (barsPassed > LimitOrderExpiryBars)
            {
                Print($"[Setup C] Invalidation: {barsPassed} bars passed since breakout. Resetting.");
                ResetSetupCState();
                return;
            }

            // Invalidation 2: Price moved too far in breakout direction (2x range width)
            double rangeWidth = _rangeHigh - _rangeLow;
            if (rangeWidth > 0)
            {
                if (_breakoutDirectionC == 1 && Symbol.Bid > _rangeHigh + rangeWidth * 2.0)
                {
                    Print("[Setup C] Invalidation: Price went too far in breakout direction (bullish). Resetting.");
                    ResetSetupCState();
                    return;
                }
                else if (_breakoutDirectionC == -1 && Symbol.Ask < _rangeLow - rangeWidth * 2.0)
                {
                    Print("[Setup C] Invalidation: Price went too far in breakout direction (bearish). Resetting.");
                    ResetSetupCState();
                    return;
                }
            }

            // Check trigger: Has closed back inside the range?
            double lastClose = Bars.ClosePrices.Last(1);
            if (_breakoutDirectionC == 1)
            {
                double triggerLevel = _rangeHigh - SetupCFadeBufferPips * Symbol.PipSize;
                if (lastClose < triggerLevel)
                {
                    Print($"[Setup C] Bullish Fakeout confirmed: last Close ({lastClose:F5}) < range high - buffer ({triggerLevel:F5}). Executing Sell Fade...");
                    ExecuteSetupCFade(TradeType.Sell);
                }
            }
            else if (_breakoutDirectionC == -1)
            {
                double triggerLevel = _rangeLow + SetupCFadeBufferPips * Symbol.PipSize;
                if (lastClose > triggerLevel)
                {
                    Print($"[Setup C] Bearish Fakeout confirmed: last Close ({lastClose:F5}) > range low + buffer ({triggerLevel:F5}). Executing Buy Fade...");
                    ExecuteSetupCFade(TradeType.Buy);
                }
            }
        }

        private void ExecuteSetupCFade(TradeType direction)
        {
            if (_tradedToday) return;

            // 1. Cancel Setup A pending order if it hasn't filled yet
            if (_pendingOrder != null)
            {
                Print($"[Setup C] Cancelling Setup A pending order {_pendingOrder.Id} due to confirmed fakeout.");
                CancelPendingOrder(_pendingOrder);
                _pendingOrder = null;
                _limitOrderPlaced = false;
                _limitOrderActive = false;
            }

            // 2. Determine SL price
            double slPrice = 0;
            if (direction == TradeType.Buy)
            {
                slPrice = _fadeExtremePrice - ExtraSlBufferPips * Symbol.PipSize;
            }
            else
            {
                slPrice = _fadeExtremePrice + ExtraSlBufferPips * Symbol.PipSize;
            }

            double entryPrice = direction == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            double stopLossPips = Math.Abs(entryPrice - slPrice) / Symbol.PipSize;
            stopLossPips = Math.Max(1.0, stopLossPips);

            // Max SL Guard
            if (SetupCMaxSlPips > 0 && stopLossPips > SetupCMaxSlPips)
            {
                Print($"[Setup C] Skip: SL too wide ({stopLossPips:F1}p > max {SetupCMaxSlPips}p)");
                ResetSetupCState();
                return;
            }

            // 3. Determine TP price
            double tpPrice = 0;
            if (direction == TradeType.Buy)
            {
                tpPrice = _rangeHigh;
            }
            else
            {
                tpPrice = _rangeLow;
            }

            double tpPips = Math.Abs(tpPrice - entryPrice) / Symbol.PipSize;
            double minTpPips = stopLossPips * SetupCRewardRiskRatio;

            // If range opposite doesn't offer enough R:R, force a minimum TP
            if (tpPips < minTpPips)
            {
                tpPips = minTpPips;
                tpPrice = direction == TradeType.Buy 
                    ? entryPrice + tpPips * Symbol.PipSize 
                    : entryPrice - tpPips * Symbol.PipSize;
            }

            // 4. Calculate Volume based on RiskPercent
            double volume = CalculateVolume(stopLossPips);
            if (volume <= 0)
            {
                Print($"[Setup C] Sizing failed: volume <= 0 (SL={stopLossPips:F1}p)");
                ResetSetupCState();
                return;
            }

            // 5. Place Market Order
            var result = ExecuteMarketOrder(direction, SymbolName, volume, LabelC, stopLossPips, tpPips);
            if (result.IsSuccessful)
            {
                _tradedToday = true;
                _limitOrderActive = false;
                _limitOrderPlaced = false;
                ResetSetupCState();
                Print($"[Setup C] Placed Market {direction} @ {entryPrice:F5} | SLPrice={slPrice:F5} ({stopLossPips:F1}p) | TPPrice={tpPrice:F5} ({tpPips:F1}p) | Vol={volume}");
            }
            else
            {
                Print($"[Setup C] Market order execution failed: {result.Error}");
                ResetSetupCState();
            }
        }

        private void ResetSetupCState()
        {
            _breakoutDetectedC = false;
            _breakoutDirectionC = 0;
            _breakoutBarIndexC = -1;
            _fadeExtremePrice = 0;
        }

        private void LogShutdownSummary()
        {
            double finalDD = (_challengeStartBal - Account.Balance) / _challengeStartBal * 100.0;

            Print(Sep);
            Print($"[Shutdown] Daniel Holmes Strategy cBot Stopped.");
            Print($"[Shutdown] Initial Balance: {_initialBalance:F2}");
            Print($"[Shutdown] Final Balance  : {Account.Balance:F2}");
            Print($"[Shutdown] Peak Balance   : {_peakBalance:F2}");
            Print($"[Shutdown] Challenge DD   : {finalDD:F2}% / {MaxTotalDrawdownPct}%");
            Print(Sep);
        }
    }
}