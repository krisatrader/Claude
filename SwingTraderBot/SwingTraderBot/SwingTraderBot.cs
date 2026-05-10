using System;
using System.Linq;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Collections;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum StrategyMode
    {
        Pullback,
        Breakout
    }

    public enum RiskType
    {
        FixedLots,
        RiskPercentage
    }

    [Robot(AccessRights = AccessRights.None, AddIndicators = true)]
    public class SwingTraderBot : Robot
    {
        [Parameter("Strategy Mode", Group = "Trading", DefaultValue = StrategyMode.Breakout)]
        public StrategyMode Mode { get; set; }

        [Parameter("Use Candle Close Confirmation", Group = "Trading", DefaultValue = true)]
        public bool UseCandleCloseConfirmation { get; set; }

        [Parameter("Risk Calculation Mode", Group = "Trading", DefaultValue = RiskType.RiskPercentage)]
        public RiskType RiskMode { get; set; }

        [Parameter("Risk Per Trade (%)", Group = "Trading", DefaultValue = 1.0, MinValue = 0.1)]
        public double RiskPerTradePct { get; set; }

        [Parameter("Fixed Volume (Lots)", Group = "Trading", DefaultValue = 0.1, MinValue = 0.01)]
        public double VolumeInLots { get; set; }

        [Parameter("Stop Loss (Pips)", Group = "Trading", DefaultValue = 30, MinValue = 1)]
        public double StopLossPips { get; set; }

        [Parameter("Take Profit (Pips)", Group = "Trading", DefaultValue = 90, MinValue = 1)]
        public double TakeProfitPips { get; set; }

        [Parameter("Fractal Period", Group = "Trading", DefaultValue = 21, MinValue = 5)]
        public int Period { get; set; }

        [Parameter("Trend EMA Period", Group = "Trading", DefaultValue = 200, MinValue = 1)]
        public int EmaPeriod { get; set; }

        [Parameter("Allow Multiple Active Trades", Group = "Trading", DefaultValue = false)]
        public bool AllowMultipleActiveTrades { get; set; }

        // --- Risk Management & Winrate Improvements ---
        [Parameter("A) Részleges Zárás (Partial Close)", Group = "Risk Management", DefaultValue = true)]
        public bool EnablePartialClose { get; set; }

        [Parameter("A) Partial Close R/R Trigger", Group = "Risk Management", DefaultValue = 1.0, MinValue = 0.5)]
        public double PartialCloseRR { get; set; }

        [Parameter("B) ATR Trailing Stop", Group = "Risk Management", DefaultValue = false)]
        public bool EnableAtrTrailingStop { get; set; }

        [Parameter("B) ATR Trailing Multiplier", Group = "Risk Management", DefaultValue = 2.0, MinValue = 0.5)]
        public double AtrTrailingMultiplier { get; set; }

        [Parameter("Enable Break-Even", Group = "Risk Management", DefaultValue = true)]
        public bool EnableBreakEven { get; set; }

        [Parameter("Break-Even Trigger (Pips)", Group = "Risk Management", DefaultValue = 25, MinValue = 5)]
        public double BreakEvenTriggerPips { get; set; }

        [Parameter("Break-Even Extra (Pips)", Group = "Risk Management", DefaultValue = 2, MinValue = 0)]
        public double BreakEvenExtraPips { get; set; }

        [Parameter("Enable Trailing Stop", Group = "Risk Management", DefaultValue = true)]
        public bool EnableTrailingStop { get; set; }

        [Parameter("Trailing Stop Trigger (Pips)", Group = "Risk Management", DefaultValue = 30, MinValue = 5)]
        public double TrailingStopTriggerPips { get; set; }

        [Parameter("Trailing Stop Distance (Pips)", Group = "Risk Management", DefaultValue = 20, MinValue = 5)]
        public double TrailingStopDistancePips { get; set; }

        [Parameter("Trailing Stop Step (Pips)", Group = "Risk Management", DefaultValue = 5, MinValue = 1)]
        public double TrailingStopStepPips { get; set; }

        // --- Filters ---
        [Parameter("D) HTF Trend Szűrő", Group = "Filters", DefaultValue = false)]
        public bool EnableHtfFilter { get; set; }

        [Parameter("D) HTF Timeframe", Group = "Filters", DefaultValue = "Hour")]
        public TimeFrame HtfTimeFrame { get; set; }

        [Parameter("E) EMA Távolság Szűrő", Group = "Filters", DefaultValue = false)]
        public bool EnableEmaDistanceFilter { get; set; }

        [Parameter("E) Max EMA Distance (ATR x)", Group = "Filters", DefaultValue = 2.0, MinValue = 0.5)]
        public double MaxEmaDistanceAtr { get; set; }

        [Parameter("F) Gyertya-Megerősítés (Pullback)", Group = "Filters", DefaultValue = false)]
        public bool EnablePullbackConfirmation { get; set; }

        // --- Session Filter ---
        [Parameter("Enable Session Filter", Group = "Trading Hours", DefaultValue = true)]
        public bool EnableSessionFilter { get; set; }

        [Parameter("Session Start Hour (0-23)", Group = "Trading Hours", DefaultValue = 9, MinValue = 0, MaxValue = 23)]
        public int SessionStartHour { get; set; }

        [Parameter("Session End Hour (0-23)", Group = "Trading Hours", DefaultValue = 20, MinValue = 0, MaxValue = 23)]
        public int SessionEndHour { get; set; }

        // --- FTMO Drawdown Limits ---
        [Parameter("Initial Account Size (USD)", Group = "FTMO & Risk", DefaultValue = 100000.0)]
        public double InitialAccountSize { get; set; }

        [Parameter("Daily Loss Limit %", Group = "FTMO & Risk", DefaultValue = 5.0)]
        public double DailyLossLimitPct { get; set; }

        [Parameter("Max Total Loss %", Group = "FTMO & Risk", DefaultValue = 10.0)]
        public double MaxDdPct { get; set; }

        [Parameter("Max Trades Per Day", Group = "FTMO & Risk", DefaultValue = 2, MinValue = 1)]
        public int MaxTradesPerDay { get; set; }

        // --- Exit Conditions ---
        [Parameter("C) Idő Alapú Zárás (Time Exit)", Group = "Exit Conditions", DefaultValue = false)]
        public bool EnableTimeBasedExit { get; set; }

        [Parameter("C) Max Bars Without Profit", Group = "Exit Conditions", DefaultValue = 8, MinValue = 1)]
        public int MaxBarsWithoutProfit { get; set; }

        [Parameter("Enable Opposite Exit", Group = "Exit Conditions", DefaultValue = true)]
        public bool EnableOppositeExit { get; set; }

        [Parameter("Exit Check Candles", Group = "Exit Conditions", DefaultValue = 10, MinValue = 1)]
        public int ExitCheckCandles { get; set; }

        [Parameter("ATR Period", Group = "Exit Conditions", DefaultValue = 14, MinValue = 1)]
        public int AtrPeriod { get; set; }

        [Parameter("Enable Momentum Exit", Group = "Exit Conditions", DefaultValue = true)]
        public bool EnableMomentumExit { get; set; }

        [Parameter("Momentum ATR Multiplier", Group = "Exit Conditions", DefaultValue = 1.5, MinValue = 0.1)]
        public double MomentumAtrMultiplier { get; set; }

        [Parameter("Enable FVG Exit", Group = "Exit Conditions", DefaultValue = true)]
        public bool EnableFvgExit { get; set; }

        [Parameter("FVG ATR Multiplier", Group = "Exit Conditions", DefaultValue = 0.5, MinValue = 0.1)]
        public double FvgAtrMultiplier { get; set; }

        // --- Logging & Monitoring ---
        [Parameter("Enable Detailed Logging", Group = "Logging & Monitoring", DefaultValue = true)]
        public bool EnableDetailedLogging { get; set; }

        [Parameter("Keep Alive Interval (Min, 0=Off)", Group = "Logging & Monitoring", DefaultValue = 60, MinValue = 0)]
        public int KeepAliveIntervalMin { get; set; }

        [Parameter("Keep Alive Message", Group = "Logging & Monitoring", DefaultValue = "Bot is running OK")]
        public string KeepAliveMessage { get; set; }

        private Fractals _fractals;
        private ExponentialMovingAverage _ema;
        private AverageTrueRange _atr;
        private Bars _htfBars;
        private ExponentialMovingAverage _htfEma;
        private HashSet<int> _partiallyClosedPositions = new HashSet<int>();
        
        private double _lastUpFractalPrice = double.NaN;
        private double _lastDownFractalPrice = double.NaN;

        private bool _upFractalTraded = false;
        private bool _downFractalTraded = false;

        // FTMO / Intraday State
        private double _initialBalance;
        private double _dayStartBalance;
        private DateTime _currentDay;
        private int _winsToday;
        private int _lossesToday;
        private int _tradesOpenedToday;
        private bool _tradingStoppedForDay;
        private DateTime _testStartDate;

        protected override void OnStart()
        {
            _fractals = Indicators.Fractals(Period);
            _ema = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaPeriod);
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Simple);
            _partiallyClosedPositions.Clear();

            if (EnableHtfFilter)
            {
                _htfBars = MarketData.GetBars(HtfTimeFrame);
                _htfEma = Indicators.ExponentialMovingAverage(_htfBars.ClosePrices, EmaPeriod);
            }

            // FTMO Initialization
            _initialBalance = InitialAccountSize;
            _dayStartBalance = Account.Balance;
            _currentDay = Server.Time.Date;
            _winsToday = 0;
            _lossesToday = 0;
            _tradesOpenedToday = 0;
            _tradingStoppedForDay = false;

            Positions.Closed += OnPositionsClosed;
            Positions.Opened += OnPositionOpened;
            Print("=================================================");
            Print($"[START] SwingTraderBot elindult.");
            _testStartDate = Server.Time;
            Print($"--- ENVIRONMENT ---");
            Print($"Symbol: {SymbolName} | Timeframe: {TimeFrame} | Start Time: {_testStartDate:yyyy-MM-dd HH:mm}");
            Print($"--- TRADING ---");
            Print($"Mode: {Mode} | Allow Multiple: {AllowMultipleActiveTrades} | Confirm: {UseCandleCloseConfirmation}");
            Print($"Risk: {RiskMode} | Risk %: {RiskPerTradePct} | Lots: {VolumeInLots}");
            Print($"SL: {StopLossPips} | TP: {TakeProfitPips} | Fractal: {Period} | EMA: {EmaPeriod}");
            Print($"--- RISK MGT ---");
            Print($"BE: {EnableBreakEven} (Trig: {BreakEvenTriggerPips}, Extra: {BreakEvenExtraPips})");
            Print($"TS: {EnableTrailingStop} (Trig: {TrailingStopTriggerPips}, Dist: {TrailingStopDistancePips}, Step: {TrailingStopStepPips})");
            Print($"--- SESSION & FTMO ---");
            Print($"Session Filter: {EnableSessionFilter} (H{SessionStartHour}-H{SessionEndHour})");
            Print($"FTMO: Init Size: {InitialAccountSize} | Daily DD: {DailyLossLimitPct}% | Max DD: {MaxDdPct}% | Max Trades: {MaxTradesPerDay}");
            Print($"--- EXIT CONDITIONS ---");
            Print($"Opposite Exit: {EnableOppositeExit} | Candles: {ExitCheckCandles} | ATR: {AtrPeriod}");
            Print($"Momentum: {EnableMomentumExit} (ATR x{MomentumAtrMultiplier}) | FVG: {EnableFvgExit} (ATR x{FvgAtrMultiplier})");
            Print($"--- FILTERS ---");
            Print($"HTF Filter: {EnableHtfFilter} ({HtfTimeFrame}) | EMA Dist: {EnableEmaDistanceFilter} (ATR x{MaxEmaDistanceAtr})");
            Print($"--- LOGGING ---");
            Print($"Detailed Log: {EnableDetailedLogging} | Keep Alive: {KeepAliveIntervalMin}m (\"{KeepAliveMessage}\")");
            Print("=================================================");

            if (KeepAliveIntervalMin > 0)
            {
                Timer.Start(KeepAliveIntervalMin * 60);
            }
        }

        protected override void OnTimer()
        {
            Print($"[KEEP ALIVE] {KeepAliveMessage} | Equity: {Math.Round(Account.Equity, 2)}");
        }

        protected override void OnStop()
        {
            Positions.Closed -= OnPositionsClosed;
            Positions.Opened -= OnPositionOpened;

            Print("=================================================");
            Print($"[STOP] SwingTraderBot leállt.");
            Print($"--- FUTÁS ÖSSZEFOGLALÓ ---");
            Print($"Symbol: {SymbolName} | Timeframe: {TimeFrame}");
            Print($"Időszak: {_testStartDate:yyyy-MM-dd HH:mm} -> {Server.Time:yyyy-MM-dd HH:mm}");
            Print($"Kötések száma: {History.Count}");
            if (History.Count > 0)
            {
                Print($"Zárt Profit: {Math.Round(History.Sum(x => x.NetProfit), 2)}");
                Print($"Win Rate: {Math.Round((double)History.Count(x => x.NetProfit > 0) / History.Count * 100, 1)}%");
            }
            Print("=================================================");
        }

        protected override void OnTick()
        {
            // Éjféli Reset (Szerver idő szerint)
            var today = Server.Time.Date;
            if (today > _currentDay)
            {
                _currentDay = today;
                _dayStartBalance = Account.Balance;
                _winsToday = 0;
                _lossesToday = 0;
                _tradesOpenedToday = 0;
                _tradingStoppedForDay = false;
                
                Print($"[RESET] Új kereskedési nap. DayStartBalance: {_dayStartBalance}");
            }

            // --- FTMO Drawdown Vészfék Ellenőrzés ---
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

                    foreach (var p in Positions.Where(x => x.SymbolName == SymbolName).ToArray())
                    {
                        ClosePosition(p);
                    }
                    
                    foreach (var o in PendingOrders.Where(x => x.SymbolName == SymbolName).ToArray())
                    {
                        CancelPendingOrder(o);
                    }
                }
            }

            // --- Profit védelem: Partial Close, Break-Even és Trailing Stop Logikák ---
            foreach (var position in Positions.Where(x => x.SymbolName == SymbolName).ToArray())
            {
                // A) Részleges Zárás (Partial Close)
                // Magyarázat: Ha a profit eléri az elvárt R/R szintet (pl. 1:1), a pozíció felét lezárjuk, hogy biztosítsuk a nyereséget.
                if (EnablePartialClose && !_partiallyClosedPositions.Contains(position.Id))
                {
                    double riskInPips = StopLossPips; 
                    if (position.Pips >= riskInPips * PartialCloseRR)
                    {
                        double volumeToClose = Symbol.NormalizeVolumeInUnits(position.VolumeInUnits / 2.0, RoundingMode.Down);
                        if (volumeToClose >= Symbol.VolumeInUnitsMin)
                        {
                            ClosePosition(position, volumeToClose);
                            _partiallyClosedPositions.Add(position.Id);
                            if (EnableDetailedLogging) Print($"[Partial Close] {position.TradeType} pozíció {PartialCloseRR}:1 R/R elérve. Felét lezártuk a biztos profit érdekében. ID: {position.Id}");
                            
                            // Automatikus Break-Even a részleges zárás után
                            double bePrice = position.EntryPrice;
                            if (position.TradeType == TradeType.Buy && (position.StopLoss == null || position.StopLoss < bePrice))
                            {
#pragma warning disable CS0618
                                ModifyPosition(position, bePrice, position.TakeProfit);
#pragma warning restore CS0618
                            }
                            else if (position.TradeType == TradeType.Sell && (position.StopLoss == null || position.StopLoss > bePrice))
                            {
#pragma warning disable CS0618
                                ModifyPosition(position, bePrice, position.TakeProfit);
#pragma warning restore CS0618
                            }
                        }
                    }
                }

                // 1. Break-Even
                if (EnableBreakEven && position.Pips >= BreakEvenTriggerPips)
                {
                    double bePrice = position.TradeType == TradeType.Buy 
                        ? position.EntryPrice + (BreakEvenExtraPips * Symbol.PipSize)
                        : position.EntryPrice - (BreakEvenExtraPips * Symbol.PipSize);
                        
                    if (position.TradeType == TradeType.Buy && (position.StopLoss == null || (double)position.StopLoss < bePrice - (0.5 * Symbol.PipSize)))
                    {
#pragma warning disable CS0618
                        ModifyPosition(position, bePrice, position.TakeProfit);
#pragma warning restore CS0618
                        if (EnableDetailedLogging) Print($"[Break-Even] Vétel levédve nullába. Új SL: {bePrice} | Aktuális Profit: {Math.Round(position.Pips, 1)} pips");
                    }
                    else if (position.TradeType == TradeType.Sell && (position.StopLoss == null || (double)position.StopLoss > bePrice + (0.5 * Symbol.PipSize)))
                    {
#pragma warning disable CS0618
                        ModifyPosition(position, bePrice, position.TakeProfit);
#pragma warning restore CS0618
                        if (EnableDetailedLogging) Print($"[Break-Even] Eladás levédve nullába. Új SL: {bePrice} | Aktuális Profit: {Math.Round(position.Pips, 1)} pips");
                    }
                }

                // B) ATR Alapú Dinamikus Trailing Stop
                // Magyarázat: A fix pipes követés helyett az aktuális piaci volatilitáshoz (ATR) igazítja a Stop Loss-t.
                if (EnableAtrTrailingStop && position.Pips >= TrailingStopTriggerPips)
                {
                    double currentAtr = _atr.Result.Last(1);
                    double atrDistance = currentAtr * AtrTrailingMultiplier;
                    
                    double newStopLoss = position.TradeType == TradeType.Buy
                        ? Symbol.Bid - atrDistance
                        : Symbol.Ask + atrDistance;

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
#pragma warning disable CS0618
                        ModifyPosition(position, newStopLoss, position.TakeProfit);
#pragma warning restore CS0618
                        if (EnableDetailedLogging) Print($"[ATR Trailing] {position.TradeType} dinamikus SL frissítve. Új SL: {Math.Round(newStopLoss, 2)}");
                    }
                }
                // 2. Trailing Stop (Step-based) - Csak ha az ATR nincs bekapcsolva
                else if (EnableTrailingStop && position.Pips >= TrailingStopTriggerPips)
                {
                    double newStopLoss = position.TradeType == TradeType.Buy
                        ? Symbol.Bid - (TrailingStopDistancePips * Symbol.PipSize)
                        : Symbol.Ask + (TrailingStopDistancePips * Symbol.PipSize);

                    bool shouldModify = false;
                    if (position.TradeType == TradeType.Buy)
                    {
                        if (position.StopLoss == null) shouldModify = true;
                        else if (newStopLoss >= position.StopLoss + (TrailingStopStepPips * Symbol.PipSize)) shouldModify = true;
                    }
                    else
                    {
                        if (position.StopLoss == null) shouldModify = true;
                        else if (newStopLoss <= position.StopLoss - (TrailingStopStepPips * Symbol.PipSize)) shouldModify = true;
                    }

                    if (shouldModify)
                    {
#pragma warning disable CS0618
                        ModifyPosition(position, newStopLoss, position.TakeProfit);
#pragma warning restore CS0618
                        if (EnableDetailedLogging) Print($"[Trailing Stop] {position.TradeType} SL frissítve. Új SL: {Math.Round(newStopLoss, 2)} | Távolság: {TrailingStopDistancePips} pip");
                    }
                }
            }
        }

        private void OnPositionOpened(PositionOpenedEventArgs args)
        {
            if (args.Position.SymbolName != SymbolName) return;

            _tradesOpenedToday++;
            
            double currentEma = _ema.Result.Last(1);
            if (EnableDetailedLogging) Print($"[NYITÁS] {args.Position.TradeType} | Ár: {args.Position.EntryPrice} | SL: {args.Position.StopLoss} | TP: {args.Position.TakeProfit} | EMA(200): {Math.Round(currentEma, 2)} | Equity: {Math.Round(Account.Equity, 2)}");
            
            CheckIntradayLimits();
        }

        private void OnPositionsClosed(PositionClosedEventArgs args)
        {
            if (args.Position.SymbolName != SymbolName) return;

            string resultType = args.Position.NetProfit > 0 ? "PROFIT" : (args.Position.NetProfit < 0 ? "VESZTESÉG" : "NULLSZALDÓ");

            if (args.Position.NetProfit > 0)
            {
                _winsToday++;
            }
            else if (args.Position.NetProfit < 0)
            {
                _lossesToday++;
            }
            
            if (EnableDetailedLogging) Print($"[ZÁRÁS - {resultType}] {args.Position.TradeType} | Pips: {Math.Round(args.Position.Pips, 1)} | Net P&L: {Math.Round(args.Position.NetProfit, 2)} | Equity: {Math.Round(Account.Equity, 2)}");
        }

        private void CheckIntradayLimits()
        {
            if (_tradingStoppedForDay) return;

            if (_tradesOpenedToday >= MaxTradesPerDay)
            {
                Print($"[Napi Limit] Elértük a napi {MaxTradesPerDay} max kötést. Mai kereskedés felfüggesztve.");
                _tradingStoppedForDay = true;
                
                foreach (var o in PendingOrders.Where(x => x.SymbolName == SymbolName).ToArray())
                {
                    CancelPendingOrder(o);
                }
            }
        }

        private bool CanOpenNewTrade()
        {
            if (_tradingStoppedForDay) return false;
            if (_tradesOpenedToday >= MaxTradesPerDay) return false;
            if (!AllowMultipleActiveTrades && Positions.Count(x => x.SymbolName == SymbolName) > 0) return false;
            return true;
        }

        private double CalculateVolume(double stopLossPips)
        {
            if (RiskMode == RiskType.FixedLots)
            {
                return Symbol.NormalizeVolumeInUnits(Symbol.QuantityToVolumeInUnits(VolumeInLots));
            }

            double riskAmount = Account.Balance * (RiskPerTradePct / 100.0);
            double exactVolume = riskAmount / (stopLossPips * Symbol.PipValue);
            
            double normalizedVolume = Symbol.NormalizeVolumeInUnits(exactVolume, RoundingMode.Down);
            
            if (normalizedVolume == 0) 
            {
                normalizedVolume = Symbol.VolumeInUnitsMin;
            }
            
            return normalizedVolume;
        }

        protected override void OnBar()
        {

            // --- Session Filter Ellenőrzés ---
            bool isWithinSession = true;
            if (EnableSessionFilter)
            {
                int currentHour = Server.Time.Hour;
                if (SessionStartHour <= SessionEndHour)
                {
                    isWithinSession = currentHour >= SessionStartHour && currentHour < SessionEndHour;
                }
                else // Átnyúlik éjfél felett
                {
                    isWithinSession = currentHour >= SessionStartHour || currentHour < SessionEndHour;
                }

                if (!isWithinSession)
                {
                    var pendingOrders = PendingOrders.Where(o => o.SymbolName == SymbolName).ToList();
                    foreach (var order in pendingOrders)
                    {
                        CancelPendingOrder(order);
                    }
                    return; 
                }
            }

            // --- Opposite Exit & Time-Based Exit Logika ---
            if (EnableOppositeExit || EnableTimeBasedExit)
            {
                double currentAtr = _atr.Result.Last(1);

                foreach (var position in Positions.Where(x => x.SymbolName == SymbolName).ToArray())
                {
                    int entryIndex = Bars.Count - 1;
                    for (int i = Bars.Count - 1; i >= 0; i--)
                    {
                        if (Bars.OpenTimes[i] <= position.EntryTime)
                        {
                            entryIndex = i;
                            break;
                        }
                    }
                    
                    int barsPassed = Bars.Count - 1 - entryIndex;

                    bool closePosition = false;
                    string exitReason = "";

                    // C) Túl sokáig tartó kötések zárása (Time-based Exit)
                    // Magyarázat: Ha a pozíció X gyertya után sem tudott profitba lendülni (nem érte el a BE szintet), zárjuk.
                    if (EnableTimeBasedExit && barsPassed >= MaxBarsWithoutProfit)
                    {
                        if (position.Pips < BreakEvenTriggerPips)
                        {
                            closePosition = true;
                            exitReason = $"[Time Exit] A pozíció {barsPassed} gyertya után sem indult el érdemben.";
                        }
                    }

                    if (!closePosition && EnableOppositeExit && barsPassed > 0 && barsPassed <= ExitCheckCandles)
                    {
                        // 1. Momentum Exit
                        if (EnableMomentumExit)
                        {
                            double body = Math.Abs(Bars.ClosePrices.Last(1) - Bars.OpenPrices.Last(1));
                            
                            if (position.TradeType == TradeType.Buy && Bars.ClosePrices.Last(1) < Bars.OpenPrices.Last(1) && body > currentAtr * MomentumAtrMultiplier)
                            {
                                closePosition = true;
                                exitReason = $"[Momentum Exit] Erős Bearish gyertya ({Math.Round(body/Symbol.PipSize, 1)} pips) a vétel ellen.";
                            }
                            else if (position.TradeType == TradeType.Sell && Bars.ClosePrices.Last(1) > Bars.OpenPrices.Last(1) && body > currentAtr * MomentumAtrMultiplier)
                            {
                                closePosition = true;
                                exitReason = $"[Momentum Exit] Erős Bullish gyertya ({Math.Round(body/Symbol.PipSize, 1)} pips) az eladás ellen.";
                            }
                        }

                        // 2. FVG Exit
                        if (!closePosition && EnableFvgExit && Bars.Count >= 3)
                        {
                            if (position.TradeType == TradeType.Buy)
                            {
                                // Bearish FVG: 2. gyertya piros, és 1. teteje < 3. alja
                                if (Bars.ClosePrices.Last(2) < Bars.OpenPrices.Last(2) && Bars.LowPrices.Last(3) > Bars.HighPrices.Last(1))
                                {
                                    double gapSize = Bars.LowPrices.Last(3) - Bars.HighPrices.Last(1);
                                    if (gapSize > currentAtr * FvgAtrMultiplier)
                                    {
                                        closePosition = true;
                                        exitReason = $"[FVG Exit] Bearish FVG ({Math.Round(gapSize/Symbol.PipSize, 1)} pips) a vétel ellen.";
                                    }
                                }
                            }
                            else if (position.TradeType == TradeType.Sell)
                            {
                                // Bullish FVG: 2. gyertya zöld, és 1. alja > 3. teteje
                                if (Bars.ClosePrices.Last(2) > Bars.OpenPrices.Last(2) && Bars.LowPrices.Last(1) > Bars.HighPrices.Last(3))
                                {
                                    double gapSize = Bars.LowPrices.Last(1) - Bars.HighPrices.Last(3);
                                    if (gapSize > currentAtr * FvgAtrMultiplier)
                                    {
                                        closePosition = true;
                                        exitReason = $"[FVG Exit] Bullish FVG ({Math.Round(gapSize/Symbol.PipSize, 1)} pips) az eladás ellen.";
                                    }
                                }
                            }
                        }
                    }

                    if (closePosition)
                    {
                        if (EnableDetailedLogging) Print($"{exitReason} Pozíció (ID: {position.Id}) zárása.");
                        ClosePosition(position);
                    }
                }
            }

            double close = Bars.ClosePrices.Last(1);
            bool isUptrend = close > _ema.Result.Last(1);
            bool isDowntrend = close < _ema.Result.Last(1);

            // D) Magasabb Idősíkos (HTF) Szűrő
            // Magyarázat: Csak akkor engedjük az Uptrend/Downtrend jelzéseket, ha a magasabb idősíkon is egyezik az irány.
            if (EnableHtfFilter && _htfBars != null && _htfEma != null)
            {
                double htfClose = _htfBars.ClosePrices.Last(1);
                double htfEmaValue = _htfEma.Result.Last(1);
                
                if (isUptrend && htfClose <= htfEmaValue) isUptrend = false;
                if (isDowntrend && htfClose >= htfEmaValue) isDowntrend = false;
            }

            // E) EMA Távolság Szűrő (Mean Reversion Filter)
            // Magyarázat: Megakadályozza a belépést, ha az ár már túlságosan elszakadt az EMA-tól (túlvettség/túladottság).
            if (EnableEmaDistanceFilter)
            {
                double currentAtr = _atr.Result.Last(1);
                double distanceToEma = Math.Abs(close - _ema.Result.Last(1));
                double maxAllowedDistance = currentAtr * MaxEmaDistanceAtr;
                
                if (distanceToEma > maxAllowedDistance)
                {
                    isUptrend = false;
                    isDowntrend = false;
                }
            }

            // ==========================================
            // --- Up Fractal Keresése (Swing High) ---
            // ==========================================
            double currentUpFractal = double.NaN;
            for (int i = 1; i <= Period + 2; i++)
            {
                if (!double.IsNaN(_fractals.UpFractal.Last(i)))
                {
                    currentUpFractal = _fractals.UpFractal.Last(i);
                    break;
                }
            }

            if (!double.IsNaN(currentUpFractal) && currentUpFractal != _lastUpFractalPrice)
            {
                _lastUpFractalPrice = currentUpFractal;
                _upFractalTraded = false; 
                
                if (Mode == StrategyMode.Breakout && !UseCandleCloseConfirmation)
                {
                    foreach (var order in PendingOrders.Where(o => o.TradeType == TradeType.Buy && o.SymbolName == SymbolName).ToArray())
                        CancelPendingOrder(order);

                    if (isUptrend && CanOpenNewTrade() && currentUpFractal > Symbol.Ask)
                    {
                        double volume = CalculateVolume(StopLossPips);
#pragma warning disable CS0618
                        PlaceStopOrder(TradeType.Buy, SymbolName, volume, currentUpFractal, "SwingBreakoutBuy", StopLossPips, TakeProfitPips);
#pragma warning restore CS0618
                        if (EnableDetailedLogging) Print($"[Breakout] Új Up Fractal azonosítva Uptrendben. Buy Stop beállítva: {currentUpFractal}");
                    }
                }
                else if (Mode == StrategyMode.Pullback)
                {
                    // F) Pullback Megerősítés: Ha be van kapcsolva, nem lép be azonnal a fractal kialakulásakor, hanem megvárja a megerősítő gyertyát lejjebb (az "Új Gyertya Zárás Logika" blokkban).
                    if (isDowntrend && CanOpenNewTrade() && !EnablePullbackConfirmation)
                    {
                        double volume = CalculateVolume(StopLossPips);
                        var result = ExecuteMarketOrder(TradeType.Sell, SymbolName, volume, "SwingPullbackSell", StopLossPips, TakeProfitPips);
                        if (result.IsSuccessful)
                        {
                            _upFractalTraded = true;
                            if (EnableDetailedLogging) Print($"[Pullback] Új Up Fractal (Csúcs) Downtrendben. Sell Market végrehajtva.");
                        }
                    }
                }
            }

            // Új Gyertya Zárás Logika (Breakout Vétel ÉS Pullback Megerősítés)
            if (isUptrend && Mode == StrategyMode.Breakout && UseCandleCloseConfirmation && !double.IsNaN(_lastUpFractalPrice) && !_upFractalTraded && CanOpenNewTrade())
            {
                if (close > _lastUpFractalPrice)
                {
                    double volume = CalculateVolume(StopLossPips);
                    var result = ExecuteMarketOrder(TradeType.Buy, SymbolName, volume, "SwingBreakoutBuy", StopLossPips, TakeProfitPips);
                    if (result.IsSuccessful)
                    {
                        _upFractalTraded = true;
                        if (EnableDetailedLogging) Print($"[Breakout] Gyertya zárás az Up Fractal felett megerősítve. Buy Market végrehajtva.");
                    }
                }
            }
            else if (isDowntrend && Mode == StrategyMode.Pullback && EnablePullbackConfirmation && !double.IsNaN(_lastUpFractalPrice) && !_upFractalTraded && CanOpenNewTrade())
            {
                // F) Pullback Megerősítés Logika: Várjuk, hogy egy gyertya lefelé zárjon (Piros gyertya) a Pullback eladáshoz
                if (close < Bars.OpenPrices.Last(1))
                {
                    double volume = CalculateVolume(StopLossPips);
                    var result = ExecuteMarketOrder(TradeType.Sell, SymbolName, volume, "SwingPullbackSell", StopLossPips, TakeProfitPips);
                    if (result.IsSuccessful)
                    {
                        _upFractalTraded = true;
                        if (EnableDetailedLogging) Print($"[Pullback Confirm] Bearish gyertya zárás az Up Fractal után. Sell Market végrehajtva.");
                    }
                }
            }

            // ==========================================
            // --- Down Fractal Keresése (Swing Low) ---
            // ==========================================
            double currentDownFractal = double.NaN;
            for (int i = 1; i <= Period + 2; i++)
            {
                if (!double.IsNaN(_fractals.DownFractal.Last(i)))
                {
                    currentDownFractal = _fractals.DownFractal.Last(i);
                    break;
                }
            }

            if (!double.IsNaN(currentDownFractal) && currentDownFractal != _lastDownFractalPrice)
            {
                _lastDownFractalPrice = currentDownFractal;
                _downFractalTraded = false; 
                
                if (Mode == StrategyMode.Breakout && !UseCandleCloseConfirmation)
                {
                    foreach (var order in PendingOrders.Where(o => o.TradeType == TradeType.Sell && o.SymbolName == SymbolName).ToArray())
                        CancelPendingOrder(order);

                    if (isDowntrend && CanOpenNewTrade() && currentDownFractal < Symbol.Bid)
                    {
                        double volume = CalculateVolume(StopLossPips);
#pragma warning disable CS0618
                        PlaceStopOrder(TradeType.Sell, SymbolName, volume, currentDownFractal, "SwingBreakoutSell", StopLossPips, TakeProfitPips);
#pragma warning restore CS0618
                        if (EnableDetailedLogging) Print($"[Breakout] Új Down Fractal azonosítva Downtrendben. Sell Stop beállítva: {currentDownFractal}");
                    }
                }
                else if (Mode == StrategyMode.Pullback)
                {
                    // F) Pullback Megerősítés: Ha be van kapcsolva, nem lép be azonnal a fractal kialakulásakor, hanem megvárja a megerősítő gyertyát feljebb.
                    if (isUptrend && CanOpenNewTrade() && !EnablePullbackConfirmation)
                    {
                        double volume = CalculateVolume(StopLossPips);
                        var result = ExecuteMarketOrder(TradeType.Buy, SymbolName, volume, "SwingPullbackBuy", StopLossPips, TakeProfitPips);
                        if (result.IsSuccessful)
                        {
                            _downFractalTraded = true;
                            if (EnableDetailedLogging) Print($"[Pullback] Új Down Fractal (Völgy) Uptrendben. Buy Market végrehajtva.");
                        }
                    }
                }
            }

            // Új Gyertya Zárás Logika (Breakout Eladás ÉS Pullback Megerősítés)
            if (isDowntrend && Mode == StrategyMode.Breakout && UseCandleCloseConfirmation && !double.IsNaN(_lastDownFractalPrice) && !_downFractalTraded && CanOpenNewTrade())
            {
                if (close < _lastDownFractalPrice)
                {
                    double volume = CalculateVolume(StopLossPips);
                    var result = ExecuteMarketOrder(TradeType.Sell, SymbolName, volume, "SwingBreakoutSell", StopLossPips, TakeProfitPips);
                    if (result.IsSuccessful)
                    {
                        _downFractalTraded = true;
                        if (EnableDetailedLogging) Print($"[Breakout] Gyertya zárás a Down Fractal alatt megerősítve. Sell Market végrehajtva.");
                    }
                }
            }
            else if (isUptrend && Mode == StrategyMode.Pullback && EnablePullbackConfirmation && !double.IsNaN(_lastDownFractalPrice) && !_downFractalTraded && CanOpenNewTrade())
            {
                // F) Pullback Megerősítés Logika: Várjuk, hogy egy gyertya felfelé zárjon (Zöld gyertya) a Pullback vételhez
                if (close > Bars.OpenPrices.Last(1))
                {
                    double volume = CalculateVolume(StopLossPips);
                    var result = ExecuteMarketOrder(TradeType.Buy, SymbolName, volume, "SwingPullbackBuy", StopLossPips, TakeProfitPips);
                    if (result.IsSuccessful)
                    {
                        _downFractalTraded = true;
                        if (EnableDetailedLogging) Print($"[Pullback Confirm] Bullish gyertya zárás a Down Fractal után. Buy Market végrehajtva.");
                    }
                }
            }
        }
    }
}