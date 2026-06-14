/// <summary>
/// NAS100 ORB + Regime Engine cBot  — v5b
/// =========================================
/// Stratégia: Opening Range Breakout (ORB) NAS100 indexre, FTMO Swing számla feltételekkel.
/// 3-modul adaptív rendszer — automatikus rezsim-váltás.
///
/// Modulok:
///   1. Smoothed Regime Engine — 16-bar EMA simított ADX/CI/PDI-NDI alapú rezsim-detektálás
///                               Trend / Range / Neutral (Transitional) állapotok
///   2. Module A — ORB Trend  — EMA200 + PDI/NDI + ORB breakout + bar-strength filter (>=60%)
///                               Csak Trend rezsimben aktív (ADX_sm>25, CI_sm<52, |PDI-NDI_sm|>10)
///   3. Module B — BB Reversion — BB(20,2) + RSI(7) + EMA200 irányfilter + ADX cap
///                               Csak Range rezsimben ÉS ADX_sm < MaxModBAdxSm (default: 32)
///                               Max 2 trade/nap, SL=ATR×1.2, TP=BB közép visszazárás
///   4. Module C — Transitional — Nincs új belépés (neutral rezsim)
///   5. Exit Engine      — Module A: Partial TP 1.5R + Donchian 10 + ATR trailing
///                         Module B: BB közép TP + SL stop
///   6. FTMO Risk Mgr    — Challenge DD guard + Grade-A pozícióméret
///   7. Cooldown         — Post-loss cooldown + napi trade limitek
///   8. News Filter      — Statikus FOMC/NFP/CPI + egyedi ablakok
///   9. Execution Engine — Limit/Market order + spread circuit breaker
///  10. Analytics        — Modul-szintű P&L, rolling win rate
///  11. Logger           — Strukturált napló
///
/// v5b változások vs v4 (Python backtest validált: Ápr+4.4%, Máj+0.9%, Jún+15.9%, FTMO DD 0%):
///   [NEW]    Module B ADX cap: ADX_sm >= MaxModBAdxSm (default 32) esetén Module B nem lép be.
///            Megakadályozza a counter-trend belépéseket magas ADX + irányváltó piacokon
///            (pl. 06-10: ADX_sm=42.7, |PDI-NDI_sm|=3.2 → OR-logika range-t adott, de a piac
///            erősen trendelő volt — v4-ben -$3,104 veszteség, v5b-ben blokkolt).
///            Backtest hatás: +$8,088 megmentett veszteség, -$2,667 elveszett nyereség → nettó +$5,421
///            Net P&L: +$21,246 (+21.25%) — legjobb az összes verzió közt.
///
/// v4.0 változások (referencia):
///   [NEW]    Smoothed Regime: ADX/CI/PDI-NDI 16-bar EMA simítás
///   [NEW]    Module B — BB Reversion: BB(20,2)+RSI(7)+EMA200 irányfilter, max 2/nap
///   [CHANGE] ORB entry: simított rezsim gate + bar-close strength filter (>=60%)
///   [CHANGE] Module B SL: ATR×1.2 (vs Module A: ATR×1.5)
///   [REMOVE] VWAP Range Engine
///
/// v3.0 változások (referencia):
///   [CHANGE] Partial TP trigger: 1.0R → 1.5R
///   [NEW]    Grade-A pozícióméret: 1.5% kockázat ha ADX>28 AND CI<45
///   [NEW]    PDI/NDI irányszerló a Trend Engine-ben
///   [NEW]    ORB minőségszerló: ORB range >= ATR×0.35
///   [NEW]    London ORB session (opcionális)
///
/// Platform: cTrader / cAlgo (.NET)
/// Instrument: NAS100 (US100) CFD
/// Timeframe: M15 (elsődleges) vagy M5
/// </summary>

using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.CentralEuropeanStandardTime, AccessRights = AccessRights.None)]
    public class NAS100_ORB_RegimeEngine_v5b : Robot
    {
        private enum RegimeMode { Trend, Range, Neutral }

        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — Smoothed Regime Detection (v4 NEW)
        // ══════════════════════════════════════════════════════════════

        [Parameter("Smooth EMA Period", Group = "Smoothed Regime", DefaultValue = 16, MinValue = 4, MaxValue = 50)]
        public int SmoothedPeriod { get; set; }

        [Parameter("Trend ADX Min (smoothed)", Group = "Smoothed Regime", DefaultValue = 25.0, MinValue = 18.0, MaxValue = 40.0)]
        public double TrendAdxMinSm { get; set; }

        [Parameter("Trend CI Max (smoothed)", Group = "Smoothed Regime", DefaultValue = 52.0, MinValue = 38.2, MaxValue = 61.8)]
        public double TrendCiMaxSm { get; set; }

        [Parameter("Trend |PDI-NDI| Min (smoothed)", Group = "Smoothed Regime", DefaultValue = 10.0, MinValue = 0.0, MaxValue = 30.0)]
        public double TrendPdiNdiMinSm { get; set; }

        [Parameter("Range ADX Max (smoothed)", Group = "Smoothed Regime", DefaultValue = 22.0, MinValue = 10.0, MaxValue = 30.0)]
        public double RangeAdxMaxSm { get; set; }

        [Parameter("Range CI Min (smoothed)", Group = "Smoothed Regime", DefaultValue = 57.0, MinValue = 45.0, MaxValue = 70.0)]
        public double RangeCiMinSm { get; set; }

        [Parameter("Range |PDI-NDI| Max (smoothed)", Group = "Smoothed Regime", DefaultValue = 6.0, MinValue = 0.0, MaxValue = 20.0)]
        public double RangePdiNdiMaxSm { get; set; }

        [Parameter("Bar Close Strength Min (ORB filter)", Group = "Smoothed Regime", DefaultValue = 0.60, MinValue = 0.40, MaxValue = 0.90)]
        public double BarStrengthMin { get; set; }

        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — ORB / Trend Engine (Module A)
        // ══════════════════════════════════════════════════════════════

        [Parameter("ADX Period", Group = "ORB Engine", DefaultValue = 14, MinValue = 5, MaxValue = 50)]
        public int AdxPeriod { get; set; }

        [Parameter("Choppiness Period", Group = "ORB Engine", DefaultValue = 14, MinValue = 5, MaxValue = 50)]
        public int ChoppinessPeriod { get; set; }

        [Parameter("ORB Session Start Hour (CET)", Group = "ORB Engine", DefaultValue = 15, MinValue = 0, MaxValue = 23)]
        public int OrbStartHour { get; set; }

        [Parameter("ORB Session Start Minute (CET)", Group = "ORB Engine", DefaultValue = 30, MinValue = 0, MaxValue = 59)]
        public int OrbStartMinute { get; set; }

        [Parameter("ORB Range Minutes (15 or 30)", Group = "ORB Engine", DefaultValue = 30, MinValue = 5, MaxValue = 60)]
        public int OrbRangeMinutes { get; set; }

        [Parameter("ORB Entry Deadline Hour (CET)", Group = "ORB Engine", DefaultValue = 18, MinValue = 16, MaxValue = 21)]
        public int OrbEntryDeadlineHour { get; set; }

        [Parameter("ORB Quality Filter (min range = ATR * mult)", Group = "ORB Engine", DefaultValue = 0.35, MinValue = 0.0, MaxValue = 1.0)]
        public double OrbQualityAtrMult { get; set; }

        [Parameter("EMA200 Period", Group = "ORB Engine", DefaultValue = 200, MinValue = 50, MaxValue = 500)]
        public int Ema200Period { get; set; }

        [Parameter("ATR Period", Group = "ORB Engine", DefaultValue = 14, MinValue = 5, MaxValue = 50)]
        public int AtrPeriod { get; set; }

        [Parameter("SL ATR Multiplier (Module A)", Group = "ORB Engine", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 4.0)]
        public double SlAtrMultiplier { get; set; }

        // ─── London ORB session (opcionális, 8:00–8:30 UTC) ──────────────────
        [Parameter("Enable London ORB Session", Group = "London ORB", DefaultValue = false)]
        public bool EnableLondonOrb { get; set; }

        [Parameter("London ORB Start Hour (UTC)", Group = "London ORB", DefaultValue = 8, MinValue = 6, MaxValue = 10)]
        public int LondonOrbStartHour { get; set; }

        [Parameter("London ORB Range Minutes", Group = "London ORB", DefaultValue = 30, MinValue = 15, MaxValue = 60)]
        public int LondonOrbRangeMinutes { get; set; }

        [Parameter("London Session Close Hour (UTC)", Group = "London ORB", DefaultValue = 14, MinValue = 12, MaxValue = 16)]
        public int LondonSessionCloseHour { get; set; }

        [Parameter("London Min |PDI-NDI| for Entry", Group = "London ORB", DefaultValue = 15.0, MinValue = 0.0, MaxValue = 30.0)]
        public double LondonPdiNdiMinDiff { get; set; }

        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — Exit Engine
        // ══════════════════════════════════════════════════════════════

        [Parameter("Donchian Exit Period", Group = "Exit Engine", DefaultValue = 10, MinValue = 5, MaxValue = 30)]
        public int DonchianExitPeriod { get; set; }

        [Parameter("ATR Trailing Multiplier", Group = "Exit Engine", DefaultValue = 2.0, MinValue = 1.0, MaxValue = 5.0)]
        public double TrailingAtrMultiplier { get; set; }

        [Parameter("Break-Even at R-Multiple", Group = "Exit Engine", DefaultValue = 1.0, MinValue = 0.5, MaxValue = 3.0)]
        public double BreakEvenRMultiple { get; set; }

        [Parameter("Enable Partial Close", Group = "Exit Engine", DefaultValue = true)]
        public bool EnablePartialClose { get; set; }

        [Parameter("Partial Close R-Trigger", Group = "Exit Engine", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 3.0)]
        public double PartialCloseRTrigger { get; set; }

        [Parameter("Partial Close % (50 = fele)", Group = "Exit Engine", DefaultValue = 50, MinValue = 25, MaxValue = 75)]
        public int PartialClosePct { get; set; }

        [Parameter("Partial Close 2nd Enable", Group = "Exit Engine", DefaultValue = false)]
        public bool EnableSecondPartialClose { get; set; }

        [Parameter("Partial Close 2nd R-Trigger", Group = "Exit Engine", DefaultValue = 2.0, MinValue = 1.0, MaxValue = 5.0)]
        public double SecondPartialCloseRTrigger { get; set; }

        [Parameter("Partial Close 2nd % (of remaining)", Group = "Exit Engine", DefaultValue = 50, MinValue = 25, MaxValue = 75)]
        public int SecondPartialClosePct { get; set; }

        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — FTMO Risk Manager
        // ══════════════════════════════════════════════════════════════

        [Parameter("Challenge Start Balance", Group = "Risk Manager", DefaultValue = 100000.0, MinValue = 1000.0, MaxValue = 2000000.0)]
        public double ChallengeStartBalance { get; set; }

        [Parameter("Risk % per Trade (Module A base)", Group = "Risk Manager", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 3.0)]
        public double RiskPercent { get; set; }

        [Parameter("Max Daily DD % (FTMO: 5)", Group = "Risk Manager", DefaultValue = 4.5, MinValue = 1.0, MaxValue = 5.0)]
        public double MaxDailyDrawdownPct { get; set; }

        [Parameter("Max Total DD % (FTMO: 10)", Group = "Risk Manager", DefaultValue = 9.0, MinValue = 3.0, MaxValue = 10.0)]
        public double MaxTotalDrawdownPct { get; set; }

        [Parameter("Max Spread (pips)", Group = "Risk Manager", DefaultValue = 5.0, MinValue = 1.0, MaxValue = 20.0)]
        public double MaxSpreadPips { get; set; }

        [Parameter("Max Position Size (units)", Group = "Risk Manager", DefaultValue = 500.0, MinValue = 1.0, MaxValue = 100000.0)]
        public double MaxPositionSizeUnits { get; set; }

        [Parameter("Grade-A Risk % (ADX>thr, CI<thr)", Group = "Risk Manager", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 5.0)]
        public double GradeARiskPercent { get; set; }

        [Parameter("Grade-A ADX Min", Group = "Risk Manager", DefaultValue = 28.0, MinValue = 20.0, MaxValue = 50.0)]
        public double GradeAAdxMin { get; set; }

        [Parameter("Grade-A CI Max", Group = "Risk Manager", DefaultValue = 45.0, MinValue = 30.0, MaxValue = 58.0)]
        public double GradeACiMax { get; set; }

        [Parameter("Post-Loss Cooldown (bars)", Group = "Risk Manager", DefaultValue = 3, MinValue = 0, MaxValue = 20)]
        public int PostLossCooldownBars { get; set; }

        [Parameter("Close Positions at Session End", Group = "Risk Manager", DefaultValue = true)]
        public bool CloseAtSessionEnd { get; set; }

        [Parameter("Session End Hour (CET)", Group = "Risk Manager", DefaultValue = 22, MinValue = 18, MaxValue = 23)]
        public int SessionEndHour { get; set; }

        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — News Filter
        // ══════════════════════════════════════════════════════════════

        [Parameter("Enable News Filter", Group = "News Filter", DefaultValue = true)]
        public bool EnableNewsFilter { get; set; }

        [Parameter("News Buffer Before (min)", Group = "News Filter", DefaultValue = 60, MinValue = 15, MaxValue = 180)]
        public int NewsBufferBeforeMin { get; set; }

        [Parameter("News Buffer After (min)", Group = "News Filter", DefaultValue = 30, MinValue = 10, MaxValue = 120)]
        public int NewsBufferAfterMin { get; set; }

        [Parameter("Block FOMC Wednesdays 19-22 CET", Group = "News Filter", DefaultValue = true)]
        public bool BlockFomcWednesdays { get; set; }

        [Parameter("Block NFP First Fridays 13-16 CET", Group = "News Filter", DefaultValue = true)]
        public bool BlockNfpFirstFridays { get; set; }

        [Parameter("Block CPI 2nd Thursdays 13-16 CET", Group = "News Filter", DefaultValue = true)]
        public bool BlockCpiSecondThursdays { get; set; }

        [Parameter("Custom Block 1 (yyyy-MM-dd HH:mm CET)", Group = "News Filter", DefaultValue = "")]
        public string CustomBlock1 { get; set; }

        [Parameter("Custom Block 2 (yyyy-MM-dd HH:mm CET)", Group = "News Filter", DefaultValue = "")]
        public string CustomBlock2 { get; set; }

        [Parameter("Custom Block 3 (yyyy-MM-dd HH:mm CET)", Group = "News Filter", DefaultValue = "")]
        public string CustomBlock3 { get; set; }

        [Parameter("Custom Block 4 (yyyy-MM-dd HH:mm CET)", Group = "News Filter", DefaultValue = "")]
        public string CustomBlock4 { get; set; }

        [Parameter("Custom Block 5 (yyyy-MM-dd HH:mm CET)", Group = "News Filter", DefaultValue = "")]
        public string CustomBlock5 { get; set; }

        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — Module B (BB Reversion, Range Engine v4)
        // ══════════════════════════════════════════════════════════════

        [Parameter("Enable Module B (BB Reversion)", Group = "Module B", DefaultValue = true)]
        public bool EnableRangeEngine { get; set; }

        [Parameter("RSI Period", Group = "Module B", DefaultValue = 7, MinValue = 3, MaxValue = 14)]
        public int RangeRsiPeriod { get; set; }

        [Parameter("RSI Oversold Level", Group = "Module B", DefaultValue = 25.0, MinValue = 10.0, MaxValue = 35.0)]
        public double RsiOversold { get; set; }

        [Parameter("RSI Overbought Level", Group = "Module B", DefaultValue = 75.0, MinValue = 65.0, MaxValue = 90.0)]
        public double RsiOverbought { get; set; }

        [Parameter("BB Period", Group = "Module B", DefaultValue = 20, MinValue = 10, MaxValue = 50)]
        public int RangeBbPeriod { get; set; }

        [Parameter("BB StdDev", Group = "Module B", DefaultValue = 2.0, MinValue = 1.0, MaxValue = 3.0)]
        public double RangeBbStdDev { get; set; }

        [Parameter("SL ATR Multiplier (Module B)", Group = "Module B", DefaultValue = 1.2, MinValue = 0.5, MaxValue = 3.0)]
        public double RangeSlAtrMult { get; set; }

        [Parameter("Risk % per Trade (Module B)", Group = "Module B", DefaultValue = 0.5, MinValue = 0.1, MaxValue = 2.0)]
        public double RangeRiskPercent { get; set; }

        [Parameter("Max Trades Per Session (Module B)", Group = "Module B", DefaultValue = 2, MinValue = 1, MaxValue = 5)]
        public int MaxRangeTradesPerSession { get; set; }

        [Parameter("Module B Max ADX_sm (ADX cap)", Group = "Module B", DefaultValue = 32.0, MinValue = 20.0, MaxValue = 50.0)]
        public double MaxModBAdxSm { get; set; }

        [Parameter("Module B Start Hour (CET)", Group = "Module B", DefaultValue = 9, MinValue = 7, MaxValue = 15)]
        public int RangeStartHour { get; set; }

        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — Execution Engine
        // ══════════════════════════════════════════════════════════════

        [Parameter("Use Limit Orders", Group = "Execution Engine", DefaultValue = true)]
        public bool UseLimitOrders { get; set; }

        [Parameter("Limit Order Offset Pips", Group = "Execution Engine", DefaultValue = 3.0, MinValue = 0.5, MaxValue = 20.0)]
        public double LimitOrderOffsetPips { get; set; }

        [Parameter("Limit Order Expiry Bars", Group = "Execution Engine", DefaultValue = 3, MinValue = 1, MaxValue = 10)]
        public int LimitOrderExpiryBars { get; set; }

        [Parameter("Max Slippage Pips", Group = "Execution Engine", DefaultValue = 5.0, MinValue = 1.0, MaxValue = 20.0)]
        public double MaxSlippagePips { get; set; }

        [Parameter("Spread Circuit Breaker (pips)", Group = "Execution Engine", DefaultValue = 8.0, MinValue = 2.0, MaxValue = 30.0)]
        public double SpreadCircuitBreakerPips { get; set; }

        [Parameter("Retry On Fail (times)", Group = "Execution Engine", DefaultValue = 2, MinValue = 0, MaxValue = 5)]
        public int ExecutionRetryCount { get; set; }

        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — Analytics
        // ══════════════════════════════════════════════════════════════

        [Parameter("Enable Analytics Log", Group = "Analytics", DefaultValue = true)]
        public bool EnableAnalytics { get; set; }

        [Parameter("Rolling Window (trades)", Group = "Analytics", DefaultValue = 20, MinValue = 5, MaxValue = 100)]
        public int AnalyticsRollingWindow { get; set; }

        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — Logger
        // ══════════════════════════════════════════════════════════════

        [Parameter("Enable Logger", Group = "Logger", DefaultValue = true)]
        public bool EnableLogger { get; set; }

        [Parameter("Heartbeat Interval (bars)", Group = "Logger", DefaultValue = 12, MinValue = 1, MaxValue = 288)]
        public int HeartbeatIntervalBars { get; set; }

        [Parameter("Log Parameter Dump on Start", Group = "Logger", DefaultValue = true)]
        public bool LogParamDumpOnStart { get; set; }

        [Parameter("Log Trade Detail Level", Group = "Logger", DefaultValue = 2, MinValue = 1, MaxValue = 3)]
        public int TradeDetailLevel { get; set; }

        // ══════════════════════════════════════════════════════════════
        // PRIVATE — Indicators
        // ══════════════════════════════════════════════════════════════

        private DirectionalMovementSystem  _adxDmi;
        private ExponentialMovingAverage   _ema200;
        private AverageTrueRange           _atr;
        private RelativeStrengthIndex      _rsiRange;
        private BollingerBands             _bbRange;

        // ══════════════════════════════════════════════════════════════
        // PRIVATE — State
        // ══════════════════════════════════════════════════════════════

        private const string Label      = "NAS100_ORB";
        private const string LabelRange = "NAS100_ORB_RANGE";

        // FTMO tracking
        private double   _initialBalance;
        private double   _challengeStartBal;
        private double   _peakBalance;
        private double   _dailyStartBalance;
        private double   _dailyStartEquity;
        private DateTime _lastDayChecked;

        // ORB state
        private double   _orbHigh        = double.MinValue;
        private double   _orbLow         = double.MaxValue;
        private bool     _orbRangeSet    = false;
        private bool     _tradedToday    = false;
        private DateTime _orbRangeEnd;
        private DateTime _lastSessionDate;

        // London ORB state
        private double   _londonOrbHigh      = double.MinValue;
        private double   _londonOrbLow       = double.MaxValue;
        private bool     _londonOrbRangeSet  = false;
        private bool     _londonTradedToday  = false;
        private DateTime _londonOrbRangeEnd;

        // Cooldown state
        private int      _cooldownBarsRemaining = 0;
        private bool     _lastTradeWasLoss      = false;

        // Break-even / Partial Close tracking (Module A)
        private double   _entryPrice           = 0;
        private double   _initialSlPips        = 0;
        private bool     _breakEvenSet         = false;
        private bool     _partialCloseDone     = false;
        private bool     _secondPartialDone    = false;
        private double   _originalVolume       = 0;

        // News Filter state
        private List<DateTime> _customBlockTimes = new List<DateTime>();

        // Smoothed Regime state (v4 NEW)
        private double   _adxSmoothed      = 0;
        private double   _ciSmoothed       = 0;
        private double   _pdiNdiSmoothed   = 0;
        private bool     _smoothedInit     = false;

        // Module B (BB Reversion) state
        private int      _rangeTradesSession = 0;   // daily counter, max MaxRangeTradesPerSession

        // Logger state
        private int      _heartbeatBarCounter = 0;
        private int      _logTradeSeq         = 0;
        private DateTime _botStartTime;

        // Entry risk tracking
        private double   _entryRiskAmount   = 0;

        // Execution Engine state
        private int      _limitOrderBarsRemaining = 0;
        private bool     _limitOrderActive  = false;

        // Analytics state
        private int      _totalTrades       = 0;
        private int      _winModA           = 0;
        private int      _lossModA          = 0;
        private int      _winModB           = 0;
        private int      _lossModB          = 0;
        private double   _totalPnlModA      = 0;
        private double   _totalPnlModB      = 0;
        private bool     _lastTradeIsRange  = false;
        private Queue<bool> _rollingResults = new Queue<bool>();

        // ══════════════════════════════════════════════════════════════
        // LIFECYCLE
        // ══════════════════════════════════════════════════════════════

        protected override void OnStart()
        {
            _adxDmi   = Indicators.DirectionalMovementSystem(AdxPeriod);
            _ema200   = Indicators.ExponentialMovingAverage(Bars.ClosePrices, Ema200Period);
            _atr      = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Simple);
            _rsiRange = Indicators.RelativeStrengthIndex(Bars.ClosePrices, RangeRsiPeriod);
            _bbRange  = Indicators.BollingerBands(Bars.ClosePrices, RangeBbPeriod, RangeBbStdDev, MovingAverageType.Simple);

            _initialBalance    = Account.Balance;
            _challengeStartBal = ChallengeStartBalance > 0 ? ChallengeStartBalance : Account.Balance;
            _peakBalance       = Account.Balance;
            _dailyStartBalance = Account.Balance;
            _dailyStartEquity  = Account.Equity;
            _lastDayChecked    = Server.Time.Date;
            _lastSessionDate   = DateTime.MinValue;
            _botStartTime      = Server.Time;

            ParseCustomBlockTimes();

            Print($"[v4] Started | Balance={_initialBalance:F2} | Challenge={_challengeStartBal:F2}");
            Print($"[v5b] ORB: {OrbStartHour}:{OrbStartMinute:D2}+{OrbRangeMinutes}min | " +
                  $"Smooth={SmoothedPeriod} | Trend ADX>{TrendAdxMinSm} CI<{TrendCiMaxSm} |PDI-NDI|>{TrendPdiNdiMinSm}");
            Print($"[v5b] Range ADX<{RangeAdxMaxSm} CI>{RangeCiMinSm} |PDI-NDI|<{RangePdiNdiMaxSm} | ModB risk={RangeRiskPercent}% SL=ATR×{RangeSlAtrMult} ADXcap={MaxModBAdxSm}");

            if (EnableLogger)
            {
                LogStartupBanner();
                if (LogParamDumpOnStart) LogParameterDump();
            }

            Positions.Closed += OnPositionClosed;
            PendingOrders.Filled += OnPendingOrderFilled;
        }

        protected override void OnBar()
        {
            // 1. Daily reset + peak update
            ResetDailyState();
            UpdatePeakBalance();
            if (EnableLogger) LogHeartbeat();

            // 2. Session end
            if (CloseAtSessionEnd && IsSessionEnd())
            {
                CloseAllPositions("Session end");
                return;
            }

            // 3. Update smoothed regime values (once per bar)
            UpdateSmoothedValues();

            // 4. Build ORB ranges
            BuildOrbRange();
            if (EnableLondonOrb) BuildLondonOrbRange();

            // 5. Manage open positions (exit logic)
            ManageOpenPositions();

            // 6. Limit order expiry
            ManageLimitOrderExpiry();

            // 7. Entry logic
            if (!IsEntryAllowed()) return;

            if (EnableLondonOrb && !_londonTradedToday && !_tradedToday)
                EvaluateLondonOrbEntry();

            // Module A — ORB Trend (Trend regime only)
            EvaluateOrbEntry();

            // Module B — BB Reversion (Range regime only)
            if (EnableRangeEngine)
                EvaluateRangeEntry();
        }

        protected override void OnTick()
        {
            ManageTrailingStop();
        }

        protected override void OnStop()
        {
            if (EnableLogger) LogShutdownSummary();
            Print($"[v4] Stopped. Final balance: {Account.Balance:F2}");
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var pos = args.Position;
            if (pos.Label != Label && pos.Label != LabelRange) return;

            if (args.Reason == PositionCloseReason.StopLoss ||
                args.Reason == PositionCloseReason.TakeProfit)
            {
                _lastTradeIsRange = (pos.Label == LabelRange);
                RecordTradeOutcome(pos);
                Print($"[Close/{args.Reason}] {pos.TradeType} | Entry={pos.EntryPrice:F2} | P&L={pos.NetProfit:F2}");
            }
        }

        private void OnPendingOrderFilled(PendingOrderFilledEventArgs args)
        {
            var pos = args.Position;
            if (pos == null) return;
            if (pos.Label != Label && pos.Label != LabelRange) return;

            _limitOrderActive = false;
            _entryPrice       = pos.EntryPrice;
            _entryRiskAmount  = Account.Balance * RiskPercent / 100.0;
            _logTradeSeq++;

            double spreadAtFill = Symbol.Spread / Symbol.PipSize;
            if (EnableLogger) LogTradeOpen(pos, 0.0, spreadAtFill);

            Print($"[Exec] Limit filled: {pos.TradeType} @ {pos.EntryPrice:F2} | " +
                  $"Vol={pos.VolumeInUnits} | Label={pos.Label}");
        }

        // ══════════════════════════════════════════════════════════════
        // MODULE 1 — SMOOTHED REGIME ENGINE (v4 NEW)
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Updates 16-bar EMA smoothed values for ADX, CI, PDI-NDI.
        /// Must be called once per bar before any regime query.
        /// </summary>
        private void UpdateSmoothedValues()
        {
            double alpha     = 2.0 / (SmoothedPeriod + 1.0);
            double adxRaw    = _adxDmi.ADX.Last(1);
            double ciRaw     = CalculateChoppiness(ChoppinessPeriod);
            double pdiNdiRaw = _adxDmi.DIPlus.Last(1) - _adxDmi.DIMinus.Last(1);

            if (!_smoothedInit)
            {
                _adxSmoothed    = adxRaw;
                _ciSmoothed     = ciRaw;
                _pdiNdiSmoothed = pdiNdiRaw;
                _smoothedInit   = true;
            }
            else
            {
                _adxSmoothed    = alpha * adxRaw    + (1.0 - alpha) * _adxSmoothed;
                _ciSmoothed     = alpha * ciRaw     + (1.0 - alpha) * _ciSmoothed;
                _pdiNdiSmoothed = alpha * pdiNdiRaw + (1.0 - alpha) * _pdiNdiSmoothed;
            }
        }

        /// <summary>
        /// Returns current regime using smoothed indicator values.
        /// Trend: ADX_sm >= 25 AND CI_sm <= 52 AND |PDI-NDI_sm| >= 10
        /// Range: ADX_sm < 22 OR CI_sm > 57 OR |PDI-NDI_sm| < 6
        /// Neutral: transitional — no new entries
        /// </summary>
        private RegimeMode GetCurrentRegime()
        {
            double absPdiNdi = Math.Abs(_pdiNdiSmoothed);

            if (_adxSmoothed >= TrendAdxMinSm && _ciSmoothed <= TrendCiMaxSm && absPdiNdi >= TrendPdiNdiMinSm)
                return RegimeMode.Trend;

            if (_adxSmoothed < RangeAdxMaxSm || _ciSmoothed > RangeCiMinSm || absPdiNdi < RangePdiNdiMaxSm)
                return RegimeMode.Range;

            return RegimeMode.Neutral;
        }

        /// <summary>
        /// Choppiness Index = 100 × LOG10(SUM(ATR,N) / (HighestHigh − LowestLow)) / LOG10(N)
        /// </summary>
        private double CalculateChoppiness(int period)
        {
            if (Bars.Count < period + 1) return 61.8;

            double atrSum      = 0;
            double highestHigh = double.MinValue;
            double lowestLow   = double.MaxValue;

            for (int i = 1; i <= period; i++)
            {
                double high      = Bars.HighPrices.Last(i);
                double low       = Bars.LowPrices.Last(i);
                double prevClose = Bars.ClosePrices.Last(i + 1);

                double tr = Math.Max(high - low,
                            Math.Max(Math.Abs(high - prevClose),
                                     Math.Abs(low  - prevClose)));
                atrSum     += tr;
                highestHigh = Math.Max(highestHigh, high);
                lowestLow   = Math.Min(lowestLow,   low);
            }

            double rangeSpan = highestHigh - lowestLow;
            if (rangeSpan <= 0) return 61.8;

            return 100.0 * Math.Log10(atrSum / rangeSpan) / Math.Log10(period);
        }

        // ══════════════════════════════════════════════════════════════
        // MODULE 2 — ORB RANGE BUILDER
        // ══════════════════════════════════════════════════════════════

        private void BuildOrbRange()
        {
            DateTime now         = Server.Time;
            DateTime sessionDate = now.Date;

            if (sessionDate != _lastSessionDate)
            {
                _orbHigh         = double.MinValue;
                _orbLow          = double.MaxValue;
                _orbRangeSet     = false;
                _tradedToday     = false;
                _lastSessionDate = sessionDate;

                DateTime orbStart = new DateTime(sessionDate.Year, sessionDate.Month, sessionDate.Day,
                                                 OrbStartHour, OrbStartMinute, 0);
                _orbRangeEnd = orbStart.AddMinutes(OrbRangeMinutes);

                Print($"[ORB] New session. Range window: {orbStart:HH:mm}–{_orbRangeEnd:HH:mm} CET");
            }

            DateTime orbWindowStart = new DateTime(sessionDate.Year, sessionDate.Month, sessionDate.Day,
                                                    OrbStartHour, OrbStartMinute, 0);

            if (now >= orbWindowStart && now < _orbRangeEnd)
            {
                _orbHigh     = Math.Max(_orbHigh, Bars.HighPrices.Last(1));
                _orbLow      = Math.Min(_orbLow,  Bars.LowPrices.Last(1));
                _orbRangeSet = false;
            }
            else if (now >= _orbRangeEnd && !_orbRangeSet
                     && _orbHigh > double.MinValue && _orbLow < double.MaxValue)
            {
                _orbRangeSet = true;
                Print($"[ORB] Range locked: High={_orbHigh:F2}, Low={_orbLow:F2}, " +
                      $"Width={((_orbHigh - _orbLow) / Symbol.PipSize):F1} pips");
            }
        }

        // ══════════════════════════════════════════════════════════════
        // MODULE 2b — LONDON ORB SESSION
        // ══════════════════════════════════════════════════════════════

        private void BuildLondonOrbRange()
        {
            DateTime nowUtc      = Server.Time.ToUniversalTime();
            DateTime dayUtc      = nowUtc.Date;
            DateTime londonStart = new DateTime(dayUtc.Year, dayUtc.Month, dayUtc.Day,
                                                LondonOrbStartHour, 0, 0, DateTimeKind.Utc);
            _londonOrbRangeEnd   = londonStart.AddMinutes(LondonOrbRangeMinutes);

            if (nowUtc >= londonStart && nowUtc < _londonOrbRangeEnd)
            {
                _londonOrbHigh     = Math.Max(_londonOrbHigh, Bars.HighPrices.Last(1));
                _londonOrbLow      = Math.Min(_londonOrbLow,  Bars.LowPrices.Last(1));
                _londonOrbRangeSet = false;
            }
            else if (nowUtc >= _londonOrbRangeEnd && !_londonOrbRangeSet
                     && _londonOrbHigh > double.MinValue)
            {
                _londonOrbRangeSet = true;
                Print($"[London] Range locked: {_londonOrbHigh:F2}/{_londonOrbLow:F2} " +
                      $"(width={(_londonOrbHigh - _londonOrbLow):F1} pts)");
            }
        }

        private void EvaluateLondonOrbEntry()
        {
            if (!_londonOrbRangeSet || _londonTradedToday || _tradedToday) return;

            DateTime nowUtc   = Server.Time.ToUniversalTime();
            DateTime closeUtc = new DateTime(nowUtc.Date.Year, nowUtc.Date.Month, nowUtc.Date.Day,
                                             LondonSessionCloseHour, 0, 0, DateTimeKind.Utc);
            if (nowUtc < _londonOrbRangeEnd || nowUtc >= closeUtc) return;

            // London ORB also uses smoothed regime
            if (GetCurrentRegime() != RegimeMode.Trend) return;

            double atrNow = _atr.Result.Last(1);
            if (OrbQualityAtrMult > 0 && (_londonOrbHigh - _londonOrbLow) < atrNow * OrbQualityAtrMult)
                return;

            double pdi  = _adxDmi.DIPlus.Last(1);
            double ndi  = _adxDmi.DIMinus.Last(1);
            double diff = Math.Abs(pdi - ndi);
            if (diff < LondonPdiNdiMinDiff)
            {
                Print($"[London] |PDI-NDI|={diff:F1} < {LondonPdiNdiMinDiff} — skip");
                return;
            }

            double ema200    = _ema200.Result.Last(1);
            double lastClose = Bars.ClosePrices.Last(1);
            bool   buLong    = lastClose > _londonOrbHigh && lastClose > ema200 && pdi > ndi;
            bool   buShort   = lastClose < _londonOrbLow  && lastClose < ema200 && ndi > pdi;

            if (!buLong && !buShort) return;

            double atrStopPips = atrNow * SlAtrMultiplier / Symbol.PipSize;
            double rangeStop   = buLong
                ? (lastClose - _londonOrbLow)  / Symbol.PipSize
                : (_londonOrbHigh - lastClose) / Symbol.PipSize;
            double stopPips = Math.Min(rangeStop, atrStopPips);
            if (stopPips <= 0) return;

            TradeType dir    = buLong ? TradeType.Buy : TradeType.Sell;
            double    volume = CalculateVolume(stopPips);
            if (volume <= 0) return;

            bool ok = ExecuteEntryOrder(dir, volume, stopPips, Label);
            if (ok)
            {
                _londonTradedToday     = true;
                _tradedToday           = true;
                _lastTradeIsRange      = false;
                _initialSlPips         = stopPips;
                _breakEvenSet          = false;
                _partialCloseDone      = false;
                _secondPartialDone     = false;
                _originalVolume        = volume;
                _cooldownBarsRemaining = 0;
                Print($"[London] {dir} | SL={stopPips:F1}p | Vol={volume} | |PDI-NDI|={diff:F1}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        // MODULE 3 — MODULE A: ORB TREND ENTRY
        // ══════════════════════════════════════════════════════════════

        private void EvaluateOrbEntry()
        {
            if (!_orbRangeSet) return;
            if (_tradedToday)  return;

            DateTime now           = Server.Time;
            DateTime entryDeadline = new DateTime(now.Date.Year, now.Date.Month, now.Date.Day,
                                                  OrbEntryDeadlineHour, 0, 0);
            if (now >= entryDeadline)
            {
                return;
            }

            // ── Smoothed Regime gate — Trend only ────────────────────
            RegimeMode regime = GetCurrentRegime();
            if (regime != RegimeMode.Trend)
            {
                Print($"[Module-A] Regime={regime} | ADX_sm={_adxSmoothed:F1} CI_sm={_ciSmoothed:F1} " +
                      $"|PDI-NDI_sm|={Math.Abs(_pdiNdiSmoothed):F1} — no ORB entry");
                return;
            }

            // ── ORB quality filter ───────────────────────────────────
            double atrNow = _atr.Result.Last(1);
            if (OrbQualityAtrMult > 0 && (_orbHigh - _orbLow) < atrNow * OrbQualityAtrMult)
            {
                Print($"[Module-A] ORB range too narrow ({(_orbHigh - _orbLow):F1} pts < ATR×{OrbQualityAtrMult}={atrNow * OrbQualityAtrMult:F1}) — skip");
                return;
            }

            // ── EMA200 trend filter ──────────────────────────────────
            double ema200    = _ema200.Result.Last(1);
            double lastClose = Bars.ClosePrices.Last(1);
            bool   bullBias  = lastClose > ema200;
            bool   bearBias  = lastClose < ema200;

            // ── PDI/NDI raw directional confirmation ─────────────────
            double pdi      = _adxDmi.DIPlus.Last(1);
            double ndi      = _adxDmi.DIMinus.Last(1);
            bool   pdiLong  = pdi > ndi;
            bool   pdiShort = ndi > pdi;

            // ── Bar close strength filter (v4 NEW) ───────────────────
            double barHigh     = Bars.HighPrices.Last(1);
            double barLow      = Bars.LowPrices.Last(1);
            double barRange    = barHigh - barLow;
            double barStrength = barRange > 0 ? (lastClose - barLow) / barRange : 0.5;

            // Long: close in top BarStrengthMin of bar | Short: close in bottom BarStrengthMin
            bool breakoutUp   = lastClose > _orbHigh && bullBias && pdiLong
                                && barStrength >= BarStrengthMin;
            bool breakoutDown = lastClose < _orbLow  && bearBias && pdiShort
                                && barStrength <= (1.0 - BarStrengthMin);

            if (!breakoutUp && !breakoutDown)
            {
                if (lastClose > _orbHigh || lastClose < _orbLow)
                    Print($"[Module-A] Breakout candidate filtered | BarStr={barStrength:F2} " +
                          $"EMA200={ema200:F2} PDI={pdi:F1} NDI={ndi:F1}");
                return;
            }

            // ── Compute stop distances ───────────────────────────────
            double atrStop = atrNow * SlAtrMultiplier;
            double rangeStop;
            TradeType direction;

            if (breakoutUp)
            {
                rangeStop = (lastClose - _orbLow)  / Symbol.PipSize;
                direction = TradeType.Buy;
            }
            else
            {
                rangeStop = (_orbHigh - lastClose) / Symbol.PipSize;
                direction = TradeType.Sell;
            }

            double atrStopPips = atrStop / Symbol.PipSize;
            double stopPips    = Math.Min(rangeStop, atrStopPips);

            if (stopPips <= 0)
            {
                Print($"[Module-A] Invalid stop ({stopPips:F1}p) — skip");
                return;
            }

            double volume = CalculateVolume(stopPips);
            if (volume <= 0) return;

            bool execOk = ExecuteEntryOrder(direction, volume, stopPips, Label);

            if (execOk)
            {
                _tradedToday           = true;
                _lastTradeIsRange      = false;
                _initialSlPips         = stopPips;
                _breakEvenSet          = false;
                _partialCloseDone      = false;
                _secondPartialDone     = false;
                _originalVolume        = volume;
                _cooldownBarsRemaining = 0;

                Print($"[Module-A] {direction} | SL={stopPips:F1}p | Vol={volume} | " +
                      $"BarStr={barStrength:F2} | ADX_sm={_adxSmoothed:F1} CI_sm={_ciSmoothed:F1} " +
                      $"|PDI-NDI_sm|={Math.Abs(_pdiNdiSmoothed):F1}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        // MODULE 4 — EXIT ENGINE
        // ══════════════════════════════════════════════════════════════

        private void ManageOpenPositions()
        {
            foreach (var pos in Positions)
            {
                if (pos.SymbolName != SymbolName) continue;

                if (pos.Label == Label)
                    ManageModuleAPosition(pos);
                else if (pos.Label == LabelRange)
                    ManageModuleBPosition(pos);
            }
        }

        /// <summary>
        /// Module A exit: Partial TP 1.5R → Break-Even → Donchian 10 decay exit.
        /// </summary>
        private void ManageModuleAPosition(Position pos)
        {
            double currentPips = pos.TradeType == TradeType.Buy
                ? (Symbol.Bid - pos.EntryPrice) / Symbol.PipSize
                : (pos.EntryPrice - Symbol.Ask) / Symbol.PipSize;

            // STEP 1: Partial Close #1
            if (EnablePartialClose && !_partialCloseDone
                && currentPips >= _initialSlPips * PartialCloseRTrigger)
            {
                double closeVolume = Symbol.NormalizeVolumeInUnits(
                    pos.VolumeInUnits * PartialClosePct / 100.0);

                if (closeVolume > 0 && closeVolume < pos.VolumeInUnits)
                {
                    ClosePosition(pos, closeVolume);
                    _partialCloseDone = true;
                    Print($"[Module-A Exit] Partial #1: {PartialClosePct}% @ {currentPips:F1}R");
                }
            }

            // STEP 2: Partial Close #2
            if (EnableSecondPartialClose && _partialCloseDone && !_secondPartialDone
                && currentPips >= _initialSlPips * SecondPartialCloseRTrigger)
            {
                double closeVolume2 = Symbol.NormalizeVolumeInUnits(
                    pos.VolumeInUnits * SecondPartialClosePct / 100.0);

                if (closeVolume2 > 0 && closeVolume2 < pos.VolumeInUnits)
                {
                    ClosePosition(pos, closeVolume2);
                    _secondPartialDone = true;
                    Print($"[Module-A Exit] Partial #2: {SecondPartialClosePct}% @ {currentPips:F1}R");
                }
            }

            // STEP 3: Break-Even
            if (!_breakEvenSet && _partialCloseDone)
            {
                ModifyPosition(pos, pos.EntryPrice, pos.TakeProfit, ProtectionType.Absolute);
                _breakEvenSet = true;
                Print($"[Module-A Exit] Break-even set @ {pos.EntryPrice:F2}");
            }
            else if (!_breakEvenSet && currentPips >= _initialSlPips * BreakEvenRMultiple)
            {
                ModifyPosition(pos, pos.EntryPrice, pos.TakeProfit, ProtectionType.Absolute);
                _breakEvenSet = true;
                Print($"[Module-A Exit] Break-even set @ {pos.EntryPrice:F2} ({BreakEvenRMultiple}R)");
            }

            // STEP 4: Donchian Decay Exit (only after break-even)
            if (_breakEvenSet)
            {
                double donchianHigh = GetDonchianHigh(DonchianExitPeriod);
                double donchianLow  = GetDonchianLow(DonchianExitPeriod);

                bool decayLong  = pos.TradeType == TradeType.Buy
                                  && Bars.ClosePrices.Last(1) < donchianLow;
                bool decayShort = pos.TradeType == TradeType.Sell
                                  && Bars.ClosePrices.Last(1) > donchianHigh;

                if (decayLong || decayShort)
                {
                    double pnl = pos.NetProfit;
                    ClosePosition(pos);
                    RecordTradeOutcome(pos);
                    Print($"[Module-A Exit] Donchian decay | {pos.TradeType} P&L={pnl:F2}");
                }
            }
        }

        /// <summary>
        /// Module B exit: TP when price bar touches BB mid. SL managed by broker stop.
        /// No partial close, no trailing, no break-even (mean reversion trade).
        /// </summary>
        private void ManageModuleBPosition(Position pos)
        {
            double bbMid   = _bbRange.Main.Last(1);
            double barHigh = Bars.HighPrices.Last(1);
            double barLow  = Bars.LowPrices.Last(1);

            bool tpHit = (pos.TradeType == TradeType.Buy  && barHigh >= bbMid)
                      || (pos.TradeType == TradeType.Sell && barLow  <= bbMid);

            if (tpHit)
            {
                double pnl = pos.NetProfit;
                _lastTradeIsRange = true;
                ClosePosition(pos);
                RecordTradeOutcome(pos);
                Print($"[Module-B Exit] BB-mid TP | {pos.TradeType} | " +
                      $"BB_mid={bbMid:F2} | P&L={pnl:F2}");
            }
        }

        private void ManageTrailingStop()
        {
            double atrValue = _atr.Result.Last(1);
            if (atrValue <= 0) return;

            double trailDistancePips = atrValue * TrailingAtrMultiplier / Symbol.PipSize;

            foreach (var pos in Positions)
            {
                if (pos.SymbolName != SymbolName) continue;
                if (pos.Label != Label) continue;        // only Module A gets trailing stop
                if (!_breakEvenSet) continue;

                if (pos.TradeType == TradeType.Buy)
                {
                    double newSl = Symbol.Bid - trailDistancePips * Symbol.PipSize;
                    if (pos.StopLoss == null || newSl > pos.StopLoss.Value)
                        ModifyPosition(pos, newSl, pos.TakeProfit, ProtectionType.Absolute);
                }
                else
                {
                    double newSl = Symbol.Ask + trailDistancePips * Symbol.PipSize;
                    if (pos.StopLoss == null || newSl < pos.StopLoss.Value)
                        ModifyPosition(pos, newSl, pos.TakeProfit, ProtectionType.Absolute);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        // MODULE 5 — FTMO RISK MANAGER
        // ══════════════════════════════════════════════════════════════

        private bool IsEntryAllowed()
        {
            if (_cooldownBarsRemaining > 0)
            {
                _cooldownBarsRemaining--;
                Print($"[Risk] Cooldown: {_cooldownBarsRemaining} bars remaining");
                return false;
            }

            if (EnableNewsFilter && IsNewsBlocked())
            {
                Print($"[NewsFilter] Entry blocked at {Server.Time:HH:mm} CET");
                return false;
            }

            double spreadPips = Symbol.Spread / Symbol.PipSize;
            if (spreadPips > MaxSpreadPips)
            {
                Print($"[Risk] Spread too wide: {spreadPips:F1}p (max {MaxSpreadPips}p)");
                return false;
            }

            double dailyDD = (_dailyStartBalance - Account.Equity) / _dailyStartBalance * 100.0;
            if (dailyDD >= MaxDailyDrawdownPct)
            {
                Print($"[Risk] DAILY DD CAP: {dailyDD:F2}% ≥ {MaxDailyDrawdownPct}% — no entries today");
                return false;
            }

            double totalDD = (_challengeStartBal - Account.Equity) / _challengeStartBal * 100.0;
            if (totalDD >= MaxTotalDrawdownPct)
            {
                Print($"[Risk] TOTAL DD CAP: {totalDD:F2}% ≥ {MaxTotalDrawdownPct}% — STOPPING");
                CloseAllPositions("FTMO total DD cap");
                Stop();
                return false;
            }

            double peakDD = (_peakBalance - Account.Equity) / _peakBalance * 100.0;
            if (peakDD >= MaxTotalDrawdownPct * 0.85)
                Print($"[Risk] PEAK DD WARNING: {peakDD:F2}% — approaching limit, peak={_peakBalance:F2}");

            // No existing open position in either module
            if (Positions.FindAll(Label, SymbolName).Length > 0) return false;
            if (Positions.FindAll(LabelRange, SymbolName).Length > 0) return false;

            return true;
        }

        // ══════════════════════════════════════════════════════════════
        // MODULE 6 — NEWS FILTER
        // ══════════════════════════════════════════════════════════════

        private bool IsNewsBlocked()
        {
            DateTime now = Server.Time;

            if (BlockFomcWednesdays && now.DayOfWeek == DayOfWeek.Wednesday)
            {
                var fomcStart = new DateTime(now.Year, now.Month, now.Day, 19, 0, 0);
                var fomcEnd   = new DateTime(now.Year, now.Month, now.Day, 22, 0, 0);
                if (now >= fomcStart && now <= fomcEnd) return true;
            }

            if (BlockNfpFirstFridays && now.DayOfWeek == DayOfWeek.Friday && now.Day <= 7)
            {
                var nfpStart = new DateTime(now.Year, now.Month, now.Day, 13, 0, 0);
                var nfpEnd   = new DateTime(now.Year, now.Month, now.Day, 16, 0, 0);
                if (now >= nfpStart && now <= nfpEnd) return true;
            }

            if (BlockCpiSecondThursdays && now.DayOfWeek == DayOfWeek.Thursday && IsSecondThursday(now))
            {
                var cpiStart = new DateTime(now.Year, now.Month, now.Day, 13, 0, 0);
                var cpiEnd   = new DateTime(now.Year, now.Month, now.Day, 16, 0, 0);
                if (now >= cpiStart && now <= cpiEnd) return true;
            }

            foreach (var blockTime in _customBlockTimes)
            {
                if (now >= blockTime.AddMinutes(-NewsBufferBeforeMin) &&
                    now <= blockTime.AddMinutes(NewsBufferAfterMin))
                    return true;
            }

            return false;
        }

        private bool IsSecondThursday(DateTime date)
        {
            int count = 0;
            for (int d = 1; d <= date.Day; d++)
            {
                if (new DateTime(date.Year, date.Month, d).DayOfWeek == DayOfWeek.Thursday)
                    count++;
            }
            return count == 2;
        }

        private void ParseCustomBlockTimes()
        {
            _customBlockTimes.Clear();
            var rawBlocks = new[] { CustomBlock1, CustomBlock2, CustomBlock3, CustomBlock4, CustomBlock5 };

            foreach (var raw in rawBlocks)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                if (DateTime.TryParseExact(raw.Trim(), "yyyy-MM-dd HH:mm",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out DateTime parsed))
                {
                    _customBlockTimes.Add(parsed);
                    Print($"[NewsFilter] Custom block: {parsed:yyyy-MM-dd HH:mm} CET ±{NewsBufferBeforeMin}/{NewsBufferAfterMin}min");
                }
                else
                {
                    Print($"[NewsFilter] Bad format (skip): '{raw}'");
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        // MODULE 7 — MODULE B: BB REVERSION (Range Regime, v4 NEW)
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Module B entry: Bollinger Bands mean reversion in Range regime.
        /// Long:  price below BB lower + RSI &lt; 25 + price above EMA200 + room to BB mid &gt; SL
        /// Short: price above BB upper + RSI &gt; 75 + price below EMA200 + room to BB mid &gt; SL
        /// SL = ATR × 1.2, TP = BB middle (managed in ManageModuleBPosition)
        /// Max MaxRangeTradesPerSession per day.
        /// </summary>
        private void EvaluateRangeEntry()
        {
            if (_rangeTradesSession >= MaxRangeTradesPerSession) return;

            // ── Time window: RangeStartHour CET to SessionEndHour CET ─
            int hour = Server.Time.Hour;
            if (hour < RangeStartHour || hour >= SessionEndHour) return;

            // ── Range regime gate (smoothed) ─────────────────────────
            RegimeMode regime = GetCurrentRegime();
            if (regime != RegimeMode.Range)
            {
                return;
            }

            // ── ADX cap (v5b): block counter-trend entries on high-ADX days ─
            if (_adxSmoothed >= MaxModBAdxSm)
            {
                Print($"[Module-B] ADX cap: ADX_sm={_adxSmoothed:F1} >= {MaxModBAdxSm} — belépés blokkolva (magas ADX + irányváltó piac)");
                return;
            }

            double lastClose = Bars.ClosePrices.Last(1);
            double rsi       = _rsiRange.Result.Last(1);
            double bbUpper   = _bbRange.Top.Last(1);
            double bbLower   = _bbRange.Bottom.Last(1);
            double bbMid     = _bbRange.Main.Last(1);
            double ema200    = _ema200.Result.Last(1);
            double atr       = _atr.Result.Last(1);
            double slDist    = atr * RangeSlAtrMult;

            bool longSignal  = false;
            bool shortSignal = false;

            // Long: below BB lower, RSI oversold, above EMA200, enough room to TP
            if (lastClose < bbLower
                && rsi < RsiOversold
                && lastClose > ema200
                && (bbMid - lastClose) > slDist)
            {
                longSignal = true;
            }

            // Short: above BB upper, RSI overbought, below EMA200, enough room to TP
            if (lastClose > bbUpper
                && rsi > RsiOverbought
                && lastClose < ema200
                && (lastClose - bbMid) > slDist)
            {
                shortSignal = true;
            }

            if (!longSignal && !shortSignal) return;

            TradeType direction = longSignal ? TradeType.Buy : TradeType.Sell;
            double stopPips     = slDist / Symbol.PipSize;
            if (stopPips <= 0) return;

            double volume = CalculateVolumeWithRisk(stopPips, RangeRiskPercent);
            if (volume <= 0) return;

            bool execOk = ExecuteEntryOrder(direction, volume, stopPips, LabelRange);

            if (execOk)
            {
                _rangeTradesSession++;
                _lastTradeIsRange  = true;
                _initialSlPips     = stopPips;
                _breakEvenSet      = false;
                _partialCloseDone  = false;
                _secondPartialDone = false;

                Print($"[Module-B] {direction} | BB_lo={bbLower:F2} BB_up={bbUpper:F2} BB_mid={bbMid:F2} | " +
                      $"RSI={rsi:F1} | EMA200={ema200:F2} | SL={stopPips:F1}p | " +
                      $"ADX_sm={_adxSmoothed:F1} CI_sm={_ciSmoothed:F1} |PDI-NDI_sm|={Math.Abs(_pdiNdiSmoothed):F1} | " +
                      $"Session trade {_rangeTradesSession}/{MaxRangeTradesPerSession}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        // MODULE 8 — EXECUTION ENGINE
        // ══════════════════════════════════════════════════════════════

        private bool ExecuteEntryOrder(TradeType direction, double volume, double stopPips, string label)
        {
            double spreadPips = Symbol.Spread / Symbol.PipSize;
            if (spreadPips > SpreadCircuitBreakerPips)
            {
                Print($"[Exec] SPREAD CB: {spreadPips:F1}p > {SpreadCircuitBreakerPips}p — blocked");
                return false;
            }

            int attempts    = 0;
            int maxAttempts = Math.Max(1, ExecutionRetryCount + 1);

            while (attempts < maxAttempts)
            {
                attempts++;

                if (UseLimitOrders)
                {
                    double limitPrice = direction == TradeType.Buy
                        ? Symbol.Ask - LimitOrderOffsetPips * Symbol.PipSize
                        : Symbol.Bid + LimitOrderOffsetPips * Symbol.PipSize;

                    double stopLossPrice = direction == TradeType.Buy
                        ? limitPrice - stopPips * Symbol.PipSize
                        : limitPrice + stopPips * Symbol.PipSize;
                    var orderResult = PlaceLimitOrder(direction, SymbolName, volume, limitPrice,
                                                      label, stopPips, null);
                    if (orderResult.IsSuccessful)
                    {
                        _limitOrderActive        = true;
                        _limitOrderBarsRemaining = LimitOrderExpiryBars;
                        Print($"[Exec] Limit {direction} @ {limitPrice:F2} | SL={stopPips:F1}p | Try={attempts}");
                        return true;
                    }
                    else
                    {
                        Print($"[Exec] Limit fail (try {attempts}): {orderResult.Error}");
                    }
                }
                else
                {
                    double preBid = Symbol.Bid;
                    double preAsk = Symbol.Ask;

                    var orderResult = ExecuteMarketOrder(direction, SymbolName, volume, label, stopPips, null);

                    if (orderResult.IsSuccessful)
                    {
                        double fillPrice    = orderResult.Position.EntryPrice;
                        double slippagePips = direction == TradeType.Buy
                            ? (fillPrice - preAsk) / Symbol.PipSize
                            : (preBid - fillPrice) / Symbol.PipSize;

                        if (slippagePips > MaxSlippagePips)
                        {
                            Print($"[Exec] SLIPPAGE: {slippagePips:F1}p > {MaxSlippagePips}p — closing");
                            ClosePosition(orderResult.Position);
                            return false;
                        }

                        Print($"[Exec] Market {direction} @ {fillPrice:F2} | Slip={slippagePips:F1}p | Spread={spreadPips:F1}p");
                        _entryPrice      = fillPrice;
                        _entryRiskAmount = Account.Balance * RiskPercent / 100.0;
                        _logTradeSeq++;
                        if (EnableLogger) LogTradeOpen(orderResult.Position, slippagePips, spreadPips);
                        return true;
                    }
                    else
                    {
                        Print($"[Exec] Market fail (try {attempts}): {orderResult.Error}");
                    }
                }
            }

            Print($"[Exec] All attempts failed ({maxAttempts}x) — entry skipped");
            return false;
        }

        private void ManageLimitOrderExpiry()
        {
            if (!_limitOrderActive) return;

            _limitOrderBarsRemaining--;

            if (_limitOrderBarsRemaining <= 0)
            {
                foreach (var order in PendingOrders)
                {
                    if (order.Label == Label || order.Label == LabelRange)
                    {
                        CancelPendingOrder(order);
                        Print($"[Exec] Limit expired & cancelled: {order.TradeType} @ {order.TargetPrice:F2}");
                    }
                }
                _limitOrderActive = false;
            }
        }

        // ══════════════════════════════════════════════════════════════
        // MODULE 9 — ANALYTICS
        // ══════════════════════════════════════════════════════════════

        private void RecordAnalytics(Position pos)
        {
            if (!EnableAnalytics) return;

            bool   isWin = pos.NetProfit > 0;
            double pnl   = pos.NetProfit;
            _totalTrades++;

            _rollingResults.Enqueue(isWin);
            if (_rollingResults.Count > AnalyticsRollingWindow)
                _rollingResults.Dequeue();

            if (_lastTradeIsRange)
            {
                if (isWin) _winModB++; else _lossModB++;
                _totalPnlModB += pnl;
            }
            else
            {
                if (isWin) _winModA++; else _lossModA++;
                _totalPnlModA += pnl;
            }

            int    wins           = 0;
            foreach (bool w in _rollingResults) if (w) wins++;
            double rollingWR = _rollingResults.Count > 0
                ? (double)wins / _rollingResults.Count * 100.0 : 0;

            int    totalA = _winModA + _lossModA;
            int    totalB = _winModB + _lossModB;
            double wrA    = totalA > 0 ? (double)_winModA / totalA * 100.0 : 0;
            double wrB    = totalB > 0 ? (double)_winModB / totalB * 100.0 : 0;

            Print($"[Analytics] Trade #{_totalTrades} | {(isWin ? "WIN" : "LOSS")} {pnl:F2} | " +
                  $"Module: {(_lastTradeIsRange ? "B" : "A")} | Rolling WR: {rollingWR:F1}%");
            Print($"[Analytics] Mod-A: {_winModA}W/{_lossModA}L ({wrA:F1}%) P&L={_totalPnlModA:F2} | " +
                  $"Mod-B: {_winModB}W/{_lossModB}L ({wrB:F1}%) P&L={_totalPnlModB:F2}");

            if (totalB >= 10 && _totalPnlModB < 0)
                Print($"[Analytics] ⚠ Module-B nettó negatív ({_totalPnlModB:F2}) {totalB} trade után — fontold meg a kikapcsolást!");
        }

        private void LogDailyAnalytics()
        {
            if (!EnableAnalytics) return;
            double totalPnl = _totalPnlModA + _totalPnlModB;
            Print($"[Analytics Daily] Trades={_totalTrades} | Mod-A={_totalPnlModA:F2} | Mod-B={_totalPnlModB:F2} | Total={totalPnl:F2}");
        }

        // ══════════════════════════════════════════════════════════════
        // HELPERS
        // ══════════════════════════════════════════════════════════════

        private void ResetDailyState()
        {
            if (Server.Time.Date == _lastDayChecked) return;

            _dailyStartBalance = Account.Balance;
            _dailyStartEquity  = Account.Equity;
            _lastDayChecked    = Server.Time.Date;
            _tradedToday       = false;
            _rangeTradesSession = 0;
            _breakEvenSet      = false;
            _partialCloseDone  = false;
            _secondPartialDone = false;
            _limitOrderActive  = false;
            _londonOrbHigh     = double.MinValue;
            _londonOrbLow      = double.MaxValue;
            _londonOrbRangeSet = false;
            _londonTradedToday = false;

            double usedDD = (_challengeStartBal - Account.Balance) / _challengeStartBal * 100.0;
            LogDailyAnalytics();
            Print($"[Daily Reset] {_lastDayChecked:yyyy-MM-dd} | Balance={_dailyStartBalance:F2} | " +
                  $"Challenge DD used={usedDD:F2}%/{MaxTotalDrawdownPct}% | Peak={_peakBalance:F2} | " +
                  $"Regime: ADX_sm={_adxSmoothed:F1} CI_sm={_ciSmoothed:F1} |PDI-NDI_sm|={Math.Abs(_pdiNdiSmoothed):F1}");
        }

        private void UpdatePeakBalance()
        {
            if (Account.Balance > _peakBalance)
            {
                _peakBalance = Account.Balance;
                Print($"[Peak] New peak: {_peakBalance:F2}");
            }
        }

        private bool IsSessionEnd()
        {
            return Server.Time.Hour >= SessionEndHour;
        }

        private void CloseAllPositions(string reason)
        {
            foreach (var pos in Positions)
            {
                if (pos.SymbolName != SymbolName) continue;
                if (pos.Label != Label && pos.Label != LabelRange) continue;
                ClosePosition(pos);
                Print($"[Risk] Closed — reason: {reason}");
            }
        }

        private void RecordTradeOutcome(Position pos)
        {
            _lastTradeWasLoss = pos.NetProfit < 0;
            if (_lastTradeWasLoss)
                _cooldownBarsRemaining = PostLossCooldownBars;

            RecordAnalytics(pos);
            if (EnableLogger) LogTradeClose(pos);
            _lastTradeIsRange = false;
        }

        private double CalculateVolume(double stopPips)
        {
            double adxNow = _adxDmi.ADX.Last(1);
            double ciNow  = CalculateChoppiness(ChoppinessPeriod);
            bool   gradeA = adxNow > GradeAAdxMin && ciNow < GradeACiMax;
            double risk   = gradeA ? GradeARiskPercent : RiskPercent;
            if (gradeA)
                Print($"[Risk] Grade-A (ADX={adxNow:F1}>{GradeAAdxMin} CI={ciNow:F1}<{GradeACiMax}) → {risk}% risk");
            return CalculateVolumeWithRisk(stopPips, risk);
        }

        private double CalculateVolumeWithRisk(double stopPips, double riskPct)
        {
            if (stopPips <= 0 || Symbol.PipValue <= 0) return 0;
            double riskAmount = Account.Balance * (riskPct / 100.0);
            double volume     = riskAmount / (stopPips * Symbol.PipValue);
            volume            = Symbol.NormalizeVolumeInUnits(volume);
            volume            = Math.Min(volume, MaxPositionSizeUnits);
            return Math.Min(volume, Symbol.VolumeInUnitsMax);
        }

        private double GetDonchianHigh(int period)
        {
            double high = double.MinValue;
            for (int i = 1; i <= period; i++)
                high = Math.Max(high, Bars.HighPrices.Last(i));
            return high;
        }

        private double GetDonchianLow(int period)
        {
            double low = double.MaxValue;
            for (int i = 1; i <= period; i++)
                low = Math.Min(low, Bars.LowPrices.Last(i));
            return low;
        }

        private void LogDdStatus()
        {
            double dailyDD   = (_dailyStartBalance - Account.Equity) / _dailyStartBalance * 100.0;
            double totalDD   = (_challengeStartBal - Account.Equity) / _challengeStartBal * 100.0;
            double peakDD    = (_peakBalance - Account.Equity) / _peakBalance * 100.0;
            Print($"[DD] Daily={dailyDD:F2}%/{MaxDailyDrawdownPct}% | Total={totalDD:F2}%/{MaxTotalDrawdownPct}% | " +
                  $"Peak={peakDD:F2}% | Equity={Account.Equity:F2}");
        }

        // ══════════════════════════════════════════════════════════════
        // MODULE 10 — LOGGER
        // ══════════════════════════════════════════════════════════════

        private static readonly string Sep  = new string('═', 72);
        private static readonly string Sep2 = new string('─', 72);

        private void LogStartupBanner()
        {
            Print(Sep);
            Print($"[LOGGER] NAS100 ORB + Regime Engine — v5b — INDULÁS");
            Print($"[LOGGER] Időpont    : {_botStartTime:yyyy-MM-dd HH:mm:ss} CET");
            Print($"[LOGGER] Számla     : {Account.Number} | {Account.BrokerName}");
            Print($"[LOGGER] Balance    : {Account.Balance:F2} {Account.Asset.Name}");
            Print($"[LOGGER] Challenge  : {_challengeStartBal:F2} {Account.Asset.Name}");
            Print($"[LOGGER] Instrument : {SymbolName} | TF: {TimeFrame}");
            Print(Sep);
        }

        private void LogParameterDump()
        {
            Print("[PARAMS] ── Smoothed Regime Detection ────────────────────────────");
            Print($"[PARAMS]  Smooth EMA Period    = {SmoothedPeriod}");
            Print($"[PARAMS]  Trend ADX Min (sm)   = {TrendAdxMinSm}");
            Print($"[PARAMS]  Trend CI Max (sm)    = {TrendCiMaxSm}");
            Print($"[PARAMS]  Trend |PDI-NDI| Min  = {TrendPdiNdiMinSm}");
            Print($"[PARAMS]  Range ADX Max (sm)   = {RangeAdxMaxSm}");
            Print($"[PARAMS]  Range CI Min (sm)    = {RangeCiMinSm}");
            Print($"[PARAMS]  Range |PDI-NDI| Max  = {RangePdiNdiMaxSm}");
            Print($"[PARAMS]  Bar Strength Min     = {BarStrengthMin:F2} (Module A)");

            Print("[PARAMS] ── Module A — ORB Trend ────────────────────────────────");
            Print($"[PARAMS]  ADX Period          = {AdxPeriod}");
            Print($"[PARAMS]  Choppiness Period   = {ChoppinessPeriod}");
            Print($"[PARAMS]  ORB Start           = {OrbStartHour}:{OrbStartMinute:D2} CET +{OrbRangeMinutes}min");
            Print($"[PARAMS]  ORB Entry Deadline  = {OrbEntryDeadlineHour}:00 CET");
            Print($"[PARAMS]  ORB Quality Filter  = ATR × {OrbQualityAtrMult}");
            Print($"[PARAMS]  EMA200 Period       = {Ema200Period}");
            Print($"[PARAMS]  ATR Period          = {AtrPeriod}");
            Print($"[PARAMS]  SL ATR Mult (Mod-A) = {SlAtrMultiplier}×");

            Print("[PARAMS] ── Exit Engine ─────────────────────────────────────────");
            Print($"[PARAMS]  Donchian Period     = {DonchianExitPeriod}");
            Print($"[PARAMS]  ATR Trail Mult      = {TrailingAtrMultiplier}×");
            Print($"[PARAMS]  Break-Even R        = {BreakEvenRMultiple}R");
            Print($"[PARAMS]  Partial Close       = {EnablePartialClose} @ {PartialCloseRTrigger}R, {PartialClosePct}%");
            Print($"[PARAMS]  2nd Partial         = {EnableSecondPartialClose} @ {SecondPartialCloseRTrigger}R, {SecondPartialClosePct}%");

            Print("[PARAMS] ── FTMO Risk Manager ──────────────────────────────────");
            Print($"[PARAMS]  Challenge Balance   = {ChallengeStartBalance:F2}");
            Print($"[PARAMS]  Risk/Trade (Mod-A)  = {RiskPercent}%");
            Print($"[PARAMS]  Grade-A Risk        = {GradeARiskPercent}% (ADX>{GradeAAdxMin} CI<{GradeACiMax})");
            Print($"[PARAMS]  Max Daily DD        = {MaxDailyDrawdownPct}%");
            Print($"[PARAMS]  Max Total DD        = {MaxTotalDrawdownPct}%");
            Print($"[PARAMS]  Max Spread          = {MaxSpreadPips}p");
            Print($"[PARAMS]  Post-Loss Cooldown  = {PostLossCooldownBars} bars");
            Print($"[PARAMS]  Session End         = {SessionEndHour}:00 CET");

            Print("[PARAMS] ── Module B — BB Reversion ────────────────────────────");
            Print($"[PARAMS]  Enabled             = {EnableRangeEngine}");
            Print($"[PARAMS]  BB({RangeBbPeriod},{RangeBbStdDev}) RSI({RangeRsiPeriod}) levels: {RsiOversold}/{RsiOverbought}");
            Print($"[PARAMS]  SL ATR Mult (Mod-B) = {RangeSlAtrMult}×");
            Print($"[PARAMS]  Risk/Trade (Mod-B)  = {RangeRiskPercent}%");
            Print($"[PARAMS]  Max per session     = {MaxRangeTradesPerSession}");
            Print($"[PARAMS]  ADX cap (MaxModBAdxSm) = {MaxModBAdxSm} [v5b]");
            Print($"[PARAMS]  Start hour          = {RangeStartHour}:00 CET");

            Print("[PARAMS] ── Execution Engine ───────────────────────────────────");
            Print($"[PARAMS]  Limit Orders        = {UseLimitOrders}");
            Print($"[PARAMS]  Limit Offset        = {LimitOrderOffsetPips}p");
            Print($"[PARAMS]  Limit Expiry        = {LimitOrderExpiryBars} bars");
            Print($"[PARAMS]  Max Slippage        = {MaxSlippagePips}p");
            Print($"[PARAMS]  Spread CB           = {SpreadCircuitBreakerPips}p");
            Print(Sep);
        }

        private void LogHeartbeat()
        {
            _heartbeatBarCounter++;
            if (_heartbeatBarCounter < HeartbeatIntervalBars) return;
            _heartbeatBarCounter = 0;

            double dailyDD    = (_dailyStartBalance - Account.Equity) / _dailyStartBalance * 100.0;
            double totalDD    = (_challengeStartBal - Account.Equity) / _challengeStartBal * 100.0;
            double spreadPips = Symbol.Spread / Symbol.PipSize;
            int    openA      = Positions.FindAll(Label, SymbolName).Length;
            int    openB      = Positions.FindAll(LabelRange, SymbolName).Length;
            RegimeMode regime = GetCurrentRegime();

            Print(Sep2);
            Print($"[HEARTBEAT] {Server.Time:yyyy-MM-dd HH:mm} | Uptime={((Server.Time - _botStartTime).TotalHours):F1}h | " +
                  $"Regime={regime} | Mod-A={openA} Mod-B={openB}");
            Print($"[HEARTBEAT] Balance={Account.Balance:F2} | Equity={Account.Equity:F2} | " +
                  $"DailyDD={dailyDD:F2}% | TotalDD={totalDD:F2}%");
            Print($"[HEARTBEAT] ADX_sm={_adxSmoothed:F1} CI_sm={_ciSmoothed:F1} |PDI-NDI_sm|={Math.Abs(_pdiNdiSmoothed):F1} | " +
                  $"Spread={spreadPips:F1}p | ORB={_orbRangeSet} | ModB_cnt={_rangeTradesSession}/{MaxRangeTradesPerSession}");

            if (TradeDetailLevel >= 3)
            {
                double adx   = _adxDmi.ADX.Last(1);
                double ci    = CalculateChoppiness(ChoppinessPeriod);
                double ema200 = _ema200.Result.Last(1);
                double atr   = _atr.Result.Last(1);
                Print($"[HEARTBEAT] Raw — ADX={adx:F1} CI={ci:F1} | EMA200={ema200:F2} ATR={atr:F2}");

                if (_orbRangeSet)
                    Print($"[HEARTBEAT] ORB High={_orbHigh:F2} Low={_orbLow:F2} Width={((_orbHigh - _orbLow) / Symbol.PipSize):F1}p");
            }

            foreach (var pos in Positions)
            {
                if (pos.SymbolName != SymbolName) continue;
                if (pos.Label != Label && pos.Label != LabelRange) continue;
                double pips = pos.TradeType == TradeType.Buy
                    ? (Symbol.Bid - pos.EntryPrice) / Symbol.PipSize
                    : (pos.EntryPrice - Symbol.Ask) / Symbol.PipSize;
                Print($"[HEARTBEAT] POS {pos.Label} {pos.TradeType} | Entry={pos.EntryPrice:F2} | " +
                      $"Pips={pips:F1} | P&L={pos.NetProfit:F2} | SL={pos.StopLoss:F2}");
            }
            Print(Sep2);
        }

        private void LogTradeOpen(Position pos, double slippagePips, double spreadAtFill)
        {
            string module = _lastTradeIsRange ? "MODULE-B (BB Reversion)" : "MODULE-A (ORB Trend)";
            double adx    = _adxDmi.ADX.Last(1);
            double ci     = CalculateChoppiness(ChoppinessPeriod);
            double atr    = _atr.Result.Last(1);
            double ema200 = _ema200.Result.Last(1);
            RegimeMode regime = GetCurrentRegime();

            Print(Sep);
            Print($"[TRADE OPEN #{_logTradeSeq}] ═ {pos.TradeType} | {module} ═");
            Print($"[TRADE OPEN #{_logTradeSeq}]  Időpont    : {Server.Time:yyyy-MM-dd HH:mm:ss} CET");
            Print($"[TRADE OPEN #{_logTradeSeq}]  Belépési ár: {pos.EntryPrice:F5}");
            Print($"[TRADE OPEN #{_logTradeSeq}]  Stop Loss  : {pos.StopLoss:F5} ({_initialSlPips:F1}p)");
            Print($"[TRADE OPEN #{_logTradeSeq}]  Volumen    : {pos.VolumeInUnits}");
            Print($"[TRADE OPEN #{_logTradeSeq}]  Regime     : {regime} | ADX_sm={_adxSmoothed:F1} CI_sm={_ciSmoothed:F1} |PDI-NDI_sm|={Math.Abs(_pdiNdiSmoothed):F1}");

            if (TradeDetailLevel >= 2)
            {
                Print($"[TRADE OPEN #{_logTradeSeq}]  ADX(raw)   : {adx:F1} | CI(raw): {ci:F1}");
                Print($"[TRADE OPEN #{_logTradeSeq}]  EMA200     : {ema200:F2} | ATR: {atr:F2}");
                Print($"[TRADE OPEN #{_logTradeSeq}]  Spread     : {spreadAtFill:F1}p | Slippage: {slippagePips:F1}p");

                if (!_lastTradeIsRange && _orbRangeSet)
                    Print($"[TRADE OPEN #{_logTradeSeq}]  ORB        : {_orbHigh:F2}/{_orbLow:F2} ({((_orbHigh - _orbLow) / Symbol.PipSize):F1}p)");

                if (_lastTradeIsRange)
                {
                    double bbU = _bbRange.Top.Last(1);
                    double bbL = _bbRange.Bottom.Last(1);
                    double bbM = _bbRange.Main.Last(1);
                    double rsi = _rsiRange.Result.Last(1);
                    Print($"[TRADE OPEN #{_logTradeSeq}]  BB         : {bbL:F2}/{bbM:F2}/{bbU:F2} | RSI={rsi:F1}");
                }
            }

            if (TradeDetailLevel >= 3)
            {
                double dailyDD = (_dailyStartBalance - Account.Equity) / _dailyStartBalance * 100.0;
                double totalDD = (_challengeStartBal - Account.Equity) / _challengeStartBal * 100.0;
                Print($"[TRADE OPEN #{_logTradeSeq}]  DailyDD    : {dailyDD:F2}% | TotalDD: {totalDD:F2}%");
                Print($"[TRADE OPEN #{_logTradeSeq}]  Balance    : {Account.Balance:F2} | Peak: {_peakBalance:F2}");
            }
            Print(Sep);
        }

        private void LogTradeClose(Position pos)
        {
            double pipsResult = (pos.VolumeInUnits > 0 && Symbol.PipValue > 0)
                ? pos.GrossProfit / (pos.VolumeInUnits * Symbol.PipValue) : 0;
            double riskBase   = _entryRiskAmount > 0 ? _entryRiskAmount : Account.Balance * RiskPercent / 100.0;
            double rMultiple  = riskBase > 0 ? pos.NetProfit / riskBase : 0;
            bool   isWin      = pos.NetProfit > 0;
            string module     = _lastTradeIsRange ? "MODULE-B" : "MODULE-A";

            Print(Sep);
            Print($"[TRADE CLOSE #{_logTradeSeq}] ═ {(isWin ? "WIN" : "LOSS")} | {pos.TradeType} | {module} ═");
            Print($"[TRADE CLOSE #{_logTradeSeq}]  Időpont    : {Server.Time:yyyy-MM-dd HH:mm:ss} CET");
            Print($"[TRADE CLOSE #{_logTradeSeq}]  Entry      : {pos.EntryPrice:F5}");
            Print($"[TRADE CLOSE #{_logTradeSeq}]  Pips       : {pipsResult:F1} | R: {rMultiple:F2}R");
            Print($"[TRADE CLOSE #{_logTradeSeq}]  Nettó P&L  : {pos.NetProfit:F2} {Account.Asset.Name}");

            if (TradeDetailLevel >= 2)
            {
                Print($"[TRADE CLOSE #{_logTradeSeq}]  Partial#1  : {_partialCloseDone} | Break-Even: {_breakEvenSet}");
                Print($"[TRADE CLOSE #{_logTradeSeq}]  Volumen    : {pos.VolumeInUnits} | SL: {pos.StopLoss:F5}");
            }

            if (TradeDetailLevel >= 3)
            {
                int    totA = _winModA + _lossModA;
                int    totB = _winModB + _lossModB;
                double wrA  = totA > 0 ? (double)_winModA / totA * 100.0 : 0;
                double wrB  = totB > 0 ? (double)_winModB / totB * 100.0 : 0;
                double dailyDD = (_dailyStartBalance - Account.Equity) / _dailyStartBalance * 100.0;
                double totalDD = (_challengeStartBal - Account.Equity) / _challengeStartBal * 100.0;
                Print($"[TRADE CLOSE #{_logTradeSeq}]  Mod-A: {_winModA}W/{_lossModA}L ({wrA:F1}%) P&L={_totalPnlModA:F2}");
                Print($"[TRADE CLOSE #{_logTradeSeq}]  Mod-B: {_winModB}W/{_lossModB}L ({wrB:F1}%) P&L={_totalPnlModB:F2}");
                Print($"[TRADE CLOSE #{_logTradeSeq}]  DailyDD={dailyDD:F2}% | TotalDD={totalDD:F2}% | Balance={Account.Balance:F2}");
            }
            Print(Sep);
        }

        private void LogShutdownSummary()
        {
            double totalPnl    = _totalPnlModA + _totalPnlModB;
            int    totalTrades = _winModA + _lossModA + _winModB + _lossModB;
            int    totalWins   = _winModA + _winModB;
            double overallWR   = totalTrades > 0 ? (double)totalWins / totalTrades * 100.0 : 0;
            double runHours    = (Server.Time - _botStartTime).TotalHours;
            double finalDD     = (_challengeStartBal - Account.Balance) / _challengeStartBal * 100.0;

            Print(Sep);
            Print($"[LOGGER] NAS100 ORB Regime Engine v4.0 — LEÁLLÁS");
            Print($"[LOGGER]  Futási idő    : {runHours:F1} óra");
            Print($"[LOGGER]  {_botStartTime:yyyy-MM-dd HH:mm} → {Server.Time:yyyy-MM-dd HH:mm} CET");
            Print(Sep2);
            Print($"[LOGGER]  Nyitó balance : {_initialBalance:F2} {Account.Asset.Name}");
            Print($"[LOGGER]  Záró balance  : {Account.Balance:F2} {Account.Asset.Name}");
            Print($"[LOGGER]  Nettó P&L     : {totalPnl:F2} {Account.Asset.Name}");
            Print($"[LOGGER]  Challenge DD  : {finalDD:F2}% / {MaxTotalDrawdownPct}%");
            Print($"[LOGGER]  Peak balance  : {_peakBalance:F2} {Account.Asset.Name}");
            Print(Sep2);
            Print($"[LOGGER]  Összes trade  : {totalTrades} | Win Rate: {overallWR:F1}% ({totalWins}W/{totalTrades - totalWins}L)");
            Print($"[LOGGER]  Module-A      : {_winModA + _lossModA} trades | {_winModA}W/{_lossModA}L | P&L={_totalPnlModA:F2}");
            Print($"[LOGGER]  Module-B      : {_winModB + _lossModB} trades | {_winModB}W/{_lossModB}L | P&L={_totalPnlModB:F2}");
            Print(Sep);
        }
    }
}