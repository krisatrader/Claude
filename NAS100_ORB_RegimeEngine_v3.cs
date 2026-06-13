/// <summary>
/// NAS100 ORB + Regime Engine cBot  — v3.0
/// =========================================
/// Stratégia: Opening Range Breakout (ORB) NAS100 indexre, FTMO Swing számla feltételekkel.
///
/// Modulok:
///   1. Regime Engine    — ADX + Choppiness Index gate (csak trending rezsimben enged be)
///   2. Trend Engine     — EMA200 + PDI/NDI irányfilter + ORB breakout entry (M5 bar-close)
///                         Opcionális London ORB session (8:00-8:30 UTC, v3 uj!)
///   3. Exit Engine      — Partial TP 1.5R (50%) + Donchian 10 decay + ATR trailing
///                         Partial TP trigger 1.5R-re emelve (v3 valtozas!)
///   4. FTMO Risk Mgr    — Equity-alapu daily 4.5% / total 9% DD guard + spread filter
///                         Grade-A pozicioméret (ADX>28 + CI<45 eseten 1.5% kockazat, v3 uj!)
///   5. Cooldown         — Napi 1 trade limit, post-loss cooldown
///   6. News Filter      — Hibrid: statikus FOMC/NFP/CPI ablakok + egyedi tiltott idoablakok
///   7. Range Engine     — VWAP +-2SD + RSI(7) + BB visszazaras (konszolidacios rezsim)
///   8. Execution Engine — Limit order entry, spread circuit breaker, slippage guard
///   9. Analytics        — Modul-szintu P&amp;L, rolling win rate, rezsim statisztika
///  10. Logger           — Strukturalt naplo: heartbeat, parameter dump, kotes reszletek
///
/// v3.0 valtozasok (backtest-alapu optimalizacio, cel: +5%/ho):
///   [CHANGE] Partial TP trigger: 1.0R -&gt; 1.5R (jobb R:R, kevesebb korai zaras)
///   [NEW]    Grade-A pozicioméret: 1.5% kockazat ha ADX&gt;GradeAAdxMin AND CI&lt;GradeACiMax
///   [NEW]    PDI/NDI iranyszerlo: long csak ha PDI&gt;NDI, short csak ha NDI&gt;PDI
///   [NEW]    ORB minosegszerlo: ORB range &gt;= ATR*OrbQualityAtrMult (gyenge range kiszurese)
///   [NEW]    London ORB session: 8:00-8:30 UTC (opcionalis, default: kikapcsolva)
///            London extra szerlo: |PDI-NDI| &gt;= LondonPdiNdiMinDiff
///
/// v2.0 valtozasok (2026):
///   [FIX]  OnPositionClosed event hozzaadva
///   [FIX]  OnPendingOrderFilled event hozzaadva
///   [FIX]  Thread.Sleep eltavolitva az ExecuteEntryOrder-bol
///   [FIX]  LogTradeClose: GrossProfit alapu pip szamitas
///   [FIX]  R-szorzo: _entryRiskAmount rogzitese belepeskor
///   [NEW]  MaxPositionSizeUnits parameter
///   [NEW]  CPI blokkolas — automatikus 2. csutortok szerlo
///
/// Platform: cTrader / cAlgo (.NET)
/// Instrument: NAS100 (US100) CFD
/// Timeframe: 5 perces (M5) — primary execution
/// </summary>

using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.CentralEuropeanStandardTime, AccessRights = AccessRights.None)]
    public class NAS100_ORB_RegimeEngine : Robot
    {
        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — Regime Engine
        // ══════════════════════════════════════════════════════════════

        [Parameter("ADX Period", Group = "Regime Engine", DefaultValue = 14, MinValue = 5, MaxValue = 50)]
        public int AdxPeriod { get; set; }

        [Parameter("ADX Threshold (min)", Group = "Regime Engine", DefaultValue = 22.0, MinValue = 15.0, MaxValue = 40.0)]
        public double AdxThreshold { get; set; }

        [Parameter("Choppiness Period", Group = "Regime Engine", DefaultValue = 14, MinValue = 5, MaxValue = 50)]
        public int ChoppinessPeriod { get; set; }

        [Parameter("Choppiness Threshold (max)", Group = "Regime Engine", DefaultValue = 55.0, MinValue = 38.2, MaxValue = 61.8)]
        public double ChoppinessThreshold { get; set; }

        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — ORB / Trend Engine
        // ══════════════════════════════════════════════════════════════

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

        [Parameter("EMA200 Period", Group = "ORB Engine", DefaultValue = 200, MinValue = 50, MaxValue = 500)]
        public int Ema200Period { get; set; }

        [Parameter("ATR Period", Group = "ORB Engine", DefaultValue = 14, MinValue = 5, MaxValue = 50)]
        public int AtrPeriod { get; set; }

        [Parameter("SL ATR Multiplier", Group = "ORB Engine", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 4.0)]
        public double SlAtrMultiplier { get; set; }

        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — Exit Engine
        // ══════════════════════════════════════════════════════════════

        [Parameter("Donchian Exit Period", Group = "Exit Engine", DefaultValue = 10, MinValue = 5, MaxValue = 30)]
        public int DonchianExitPeriod { get; set; }

        [Parameter("ATR Trailing Multiplier", Group = "Exit Engine", DefaultValue = 2.0, MinValue = 1.0, MaxValue = 5.0)]
        public double TrailingAtrMultiplier { get; set; }

        [Parameter("Break-Even at R-Multiple", Group = "Exit Engine", DefaultValue = 1.0, MinValue = 0.5, MaxValue = 3.0)]
        public double BreakEvenRMultiple { get; set; }

        // ── Partial Close ─────────────────────────────────────────────
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

        [Parameter("Risk % per Trade", Group = "Risk Manager", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 3.0)]
        public double RiskPercent { get; set; }

        [Parameter("Max Daily DD % (FTMO: 5)", Group = "Risk Manager", DefaultValue = 4.5, MinValue = 1.0, MaxValue = 5.0)]
        public double MaxDailyDrawdownPct { get; set; }

        [Parameter("Max Total DD % (FTMO: 10)", Group = "Risk Manager", DefaultValue = 9.0, MinValue = 3.0, MaxValue = 10.0)]
        public double MaxTotalDrawdownPct { get; set; }

        [Parameter("Max Spread (pips)", Group = "Risk Manager", DefaultValue = 5.0, MinValue = 1.0, MaxValue = 20.0)]
        public double MaxSpreadPips { get; set; }

        [Parameter("Max Position Size (units)", Group = "Risk Manager", DefaultValue = 500.0, MinValue = 1.0, MaxValue = 100000.0)]
        public double MaxPositionSizeUnits { get; set; }

        [Parameter("Grade-A Risk % (ADX>threshold, CI<threshold)", Group = "Risk Manager", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 5.0)]
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
        // PARAMETERS — News Filter (Modul 6)
        // ══════════════════════════════════════════════════════════════

        [Parameter("Enable News Filter", Group = "News Filter", DefaultValue = true)]
        public bool EnableNewsFilter { get; set; }

        [Parameter("News Buffer Before (min)", Group = "News Filter", DefaultValue = 60, MinValue = 15, MaxValue = 180)]
        public int NewsBufferBeforeMin { get; set; }

        [Parameter("News Buffer After (min)", Group = "News Filter", DefaultValue = 30, MinValue = 10, MaxValue = 120)]
        public int NewsBufferAfterMin { get; set; }

        // FOMC — minden évben kb. 8 alkalom, szerdánként 20:00 CET
        [Parameter("Block FOMC Wednesdays 19-22 CET", Group = "News Filter", DefaultValue = true)]
        public bool BlockFomcWednesdays { get; set; }

        // NFP — minden hónap első péntekje 14:30 CET
        [Parameter("Block NFP First Fridays 13-16 CET", Group = "News Filter", DefaultValue = true)]
        public bool BlockNfpFirstFridays { get; set; }

        // CPI — általában minden hónap 2. csütörtökén 14:30 CET (US CPI)
        [Parameter("Block CPI 2nd Thursdays 13-16 CET", Group = "News Filter", DefaultValue = true)]
        public bool BlockCpiSecondThursdays { get; set; }

        // Egyedi tiltott időablakok — formátum: "yyyy-MM-dd HH:mm" CET, üres = inaktív
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
        // PARAMETERS — Range Engine (Modul 7)
        // ══════════════════════════════════════════════════════════════

        [Parameter("Enable Range Engine", Group = "Range Engine", DefaultValue = true)]
        public bool EnableRangeEngine { get; set; }

        [Parameter("Range CI Threshold (min)", Group = "Range Engine", DefaultValue = 55.0, MinValue = 45.0, MaxValue = 61.8)]
        public double RangeChoppinessMin { get; set; }

        [Parameter("Range ADX Threshold (max)", Group = "Range Engine", DefaultValue = 18.0, MinValue = 10.0, MaxValue = 25.0)]
        public double RangeAdxMax { get; set; }

        [Parameter("VWAP SD Band", Group = "Range Engine", DefaultValue = 2.0, MinValue = 1.0, MaxValue = 3.0)]
        public double VwapSdBand { get; set; }

        [Parameter("RSI Period (Range)", Group = "Range Engine", DefaultValue = 7, MinValue = 3, MaxValue = 14)]
        public int RangeRsiPeriod { get; set; }

        [Parameter("RSI Oversold Level", Group = "Range Engine", DefaultValue = 25.0, MinValue = 10.0, MaxValue = 35.0)]
        public double RsiOversold { get; set; }

        [Parameter("RSI Overbought Level", Group = "Range Engine", DefaultValue = 75.0, MinValue = 65.0, MaxValue = 90.0)]
        public double RsiOverbought { get; set; }

        [Parameter("BB Period (Range)", Group = "Range Engine", DefaultValue = 20, MinValue = 10, MaxValue = 50)]
        public int RangeBbPeriod { get; set; }

        [Parameter("BB StdDev", Group = "Range Engine", DefaultValue = 2.0, MinValue = 1.0, MaxValue = 3.0)]
        public double RangeBbStdDev { get; set; }

        [Parameter("Range Risk % (vs Trend %)", Group = "Range Engine", DefaultValue = 0.5, MinValue = 0.1, MaxValue = 1.5)]
        public double RangeRiskPercent { get; set; }

        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — Execution Engine (Modul 8)
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
        // PARAMETERS — Analytics (Modul 9)
        // ══════════════════════════════════════════════════════════════

        [Parameter("Enable Analytics Log", Group = "Analytics", DefaultValue = true)]
        public bool EnableAnalytics { get; set; }

        [Parameter("Rolling Window (trades)", Group = "Analytics", DefaultValue = 20, MinValue = 5, MaxValue = 100)]
        public int AnalyticsRollingWindow { get; set; }

        // ══════════════════════════════════════════════════════════════
        // PARAMETERS — Logger (Modul 10)
        // ══════════════════════════════════════════════════════════════

        [Parameter("Enable Logger", Group = "Logger", DefaultValue = true)]
        public bool EnableLogger { get; set; }

        [Parameter("Heartbeat Interval (bars)", Group = "Logger", DefaultValue = 12, MinValue = 1, MaxValue = 288)]
        public int HeartbeatIntervalBars { get; set; }

        [Parameter("Log Parameter Dump on Start", Group = "Logger", DefaultValue = true)]
        public bool LogParamDumpOnStart { get; set; }

        [Parameter("Log Trade Detail Level", Group = "Logger", DefaultValue = 2, MinValue = 1, MaxValue = 3)]
        public int TradeDetailLevel { get; set; }
        // 1 = alapszint (entry/exit ár, P&L)
        // 2 = közepes (+ rezsim, indikátorok, stop szint)
        // 3 = teljes (+ DD státusz, volumen, összes szűrő eredménye)


        // ══════════════════════════════════════════════════════════════
        // PRIVATE — Indicators
        // ══════════════════════════════════════════════════════════════

        private DirectionalMovementSystem  _adxDmi;
        private ExponentialMovingAverage   _ema200;
        private AverageTrueRange           _atr;
        private RelativeStrengthIndex      _rsiRange;
        private BollingerBands             _bbRange;

        // Donchian channels computed manually (high/low over N bars)
        // cTrader does not expose a built-in Donchian indicator via API

        // ══════════════════════════════════════════════════════════════
        // PRIVATE — State
        // ══════════════════════════════════════════════════════════════

        private const string Label = "NAS100_ORB";

        // FTMO tracking
        private double   _initialBalance;       // OnStart() pillanatában rögzített balance
        private double   _challengeStartBal;    // Paraméterből: challenge indulásakor lévő egyenleg
        private double   _peakBalance;          // Futó csúcs balance (OnBar/OnTick minden hívásnál frissül)
        private double   _dailyStartBalance;    // Nap eleji balance (ResetDailyState-ben frissül)
        private double   _dailyStartEquity;     // Nap eleji equity (lebegő pozíciók nélkül)
        private DateTime _lastDayChecked;

        // ORB state (resets each session)
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

        // Break-even és Partial Close tracking
        private double   _entryPrice           = 0;
        private double   _initialSlPips        = 0;
        private bool     _breakEvenSet         = false;
        private bool     _partialCloseDone     = false;   // 1. partial close megtörtént-e
        private bool     _secondPartialDone    = false;   // 2. partial close megtörtént-e
        private double   _originalVolume       = 0;       // Belépéskori teljes volume (lots)

        // News Filter state
        private List<DateTime> _customBlockTimes = new List<DateTime>();

        // Logger state
        private int      _heartbeatBarCounter = 0;
        private int      _logTradeSeq         = 0;   // szekvenciális trade azonosító
        private DateTime _botStartTime;

        // Range Engine state
        private double   _vwapValue         = 0;
        private double   _vwapSumPV         = 0;    // Σ(Price × Volume)
        private double   _vwapSumV          = 0;    // Σ(Volume)
        private double   _vwapSumPV2        = 0;    // Σ(Price² × Volume) for variance
        private int      _vwapBarCount      = 0;
        private DateTime _vwapSessionDate   = DateTime.MinValue;
        private bool     _rangeTradeToday   = false;

        // Entry risk tracking (for accurate R-multiple calculation)
        private double   _entryRiskAmount   = 0;

        // Execution Engine state
        private int      _pendingOrderId    = 0;
        private int      _limitOrderBarsRemaining = 0;
        private bool     _limitOrderActive  = false;

        // Analytics state
        private int      _totalTrades       = 0;
        private int      _winTrend          = 0;
        private int      _lossTrend         = 0;
        private int      _winRange          = 0;
        private int      _lossRange         = 0;
        private double   _totalPnlTrend     = 0;
        private double   _totalPnlRange     = 0;
        private bool     _lastTradeIsRange  = false;
        private Queue<bool> _rollingResults = new Queue<bool>();  // true=win

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

            _botStartTime = Server.Time;

            Print($"[{Label}] Started. Bot balance: {_initialBalance:F2} | Challenge start: {_challengeStartBal:F2}");
            Print($"[{Label}] ORB window: {OrbStartHour}:{OrbStartMinute:D2} CET + {OrbRangeMinutes} min");

            // Parse custom news block times
            ParseCustomBlockTimes();
            Print($"[NewsFilter] Loaded {_customBlockTimes.Count} custom block window(s).");

            // Logger: indulási napló
            if (EnableLogger)
            {
                LogStartupBanner();
                if (LogParamDumpOnStart) LogParameterDump();
            }
        }

        protected override void OnBar()
        {
            // ── 1. Daily reset ───────────────────────────────────────
            ResetDailyState();

            // ── 1b. Continuous peak balance update ───────────────────
            UpdatePeakBalance();

            // ── 1c. Logger heartbeat ──────────────────────────────────
            if (EnableLogger) LogHeartbeat();

            // ── 2. Session end — close open positions ────────────────
            if (CloseAtSessionEnd && IsSessionEnd())
            {
                CloseAllPositions("Session end");
                return;
            }

            // ── 3. Build ORB range during the opening window ─────────
            BuildOrbRange();
            if (EnableLondonOrb) BuildLondonOrbRange();

            // ── 4. Manage open positions (exit logic) ────────────────
            ManageOpenPositions();

            // ── 5. VWAP folyamatos frissítése ────────────────────────
            UpdateVwap();

            // ── 6. Limit order lejárat kezelése ──────────────────────
            ManageLimitOrderExpiry();

            // ── 7. Entry logic ────────────────────────────────────────
            if (!IsEntryAllowed()) return;

            // London ORB — opcionális 2. session (8:00-8:30 UTC)
            if (EnableLondonOrb && !_londonTradedToday && !_tradedToday)
                EvaluateLondonOrbEntry();

            // NY Trend Engine (ORB) — csak trending rezsimben
            EvaluateOrbEntry();

            // Range Engine — csak konszolidációs rezsimben, ha nem volt ma trend trade
            if (EnableRangeEngine && !_tradedToday)
                EvaluateRangeEntry();
        }

        protected override void OnTick()
        {
            // Trailing stop management on every tick for precision
            ManageTrailingStop();
        }

        protected override void OnStop()
        {
            if (EnableLogger) LogShutdownSummary();
            Print($"[{Label}] Stopped. Final balance: {Account.Balance:F2}");
        }

        // ── OnPositionClosed: handles SL/TP closures that bypass ManageOpenPositions ──
        // Without this, analytics and cooldown NEVER fire on stop-loss or take-profit hits.
        protected override void OnPositionClosed(PositionClosedEventArgs args)
        {
            var pos = args.Position;
            if (pos.Label != Label && pos.Label != "NAS100_ORB_RANGE") return;

            // Only handle SL/TP — Donchian/session-end exits already call RecordTradeOutcome
            if (args.Reason == PositionCloseReason.StopLoss ||
                args.Reason == PositionCloseReason.TakeProfit)
            {
                _lastTradeIsRange = (pos.Label == "NAS100_ORB_RANGE");
                RecordTradeOutcome(pos);
                Print($"[Close/{args.Reason}] {pos.TradeType} | " +
                      $"Entry={pos.EntryPrice:F2} | P&L={pos.NetProfit:F2}");
            }
        }

        // ── OnPendingOrderFilled: sets up position state when a limit order fills ──
        // Without this, _entryPrice/_logTradeSeq/LogTradeOpen are never called for limit entries.
        protected override void OnPendingOrderFilled(PendingOrderFilledEventArgs args)
        {
            var pos = args.Position;
            if (pos == null) return;
            if (pos.Label != Label && pos.Label != "NAS100_ORB_RANGE") return;

            _limitOrderActive = false;
            _entryPrice       = pos.EntryPrice;
            _entryRiskAmount  = Account.Balance * RiskPercent / 100.0;
            _logTradeSeq++;

            double spreadAtFill = Symbol.Spread / Symbol.PipSize;
            if (EnableLogger) LogTradeOpen(pos, 0.0, spreadAtFill);

            Print($"[Exec] Limit order filled: {pos.TradeType} @ {pos.EntryPrice:F2} | " +
                  $"Vol={pos.VolumeInUnits} | Label={pos.Label}");
        }

        // ══════════════════════════════════════════════════════════════
        // MODULE 1 — REGIME ENGINE
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns true if the market is in a trending regime suitable for ORB entry.
        /// Gate: Choppiness Index below threshold AND ADX above threshold.
        /// </summary>
        private bool IsTrendingRegime()
        {
            double adx         = _adxDmi.ADX.Last(1);
            double choppiness  = CalculateChoppiness(ChoppinessPeriod);

            bool trending = adx > AdxThreshold && choppiness < ChoppinessThreshold;

            if (!trending)
                Print($"[Regime] Flat — ADX: {adx:F1} (need >{AdxThreshold}), " +
                      $"CI: {choppiness:F1} (need <{ChoppinessThreshold})");

            return trending;
        }

        /// <summary>
        /// Choppiness Index = 100 × LOG10(SUM(ATR,N) / (HighestHigh - LowestLow)) / LOG10(N)
        /// Range: 100 = max choppy, 0 = max trending. Threshold ~38.2–61.8.
        /// </summary>
        private double CalculateChoppiness(int period)
        {
            if (Bars.Count < period + 1) return 61.8; // default to choppy if insufficient data

            double atrSum    = 0;
            double highestHigh = double.MinValue;
            double lowestLow   = double.MaxValue;

            for (int i = 1; i <= period; i++)
            {
                double high  = Bars.HighPrices.Last(i);
                double low   = Bars.LowPrices.Last(i);
                double prevClose = Bars.ClosePrices.Last(i + 1);

                double trueRange = Math.Max(high - low,
                                   Math.Max(Math.Abs(high - prevClose),
                                            Math.Abs(low  - prevClose)));
                atrSum     += trueRange;
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

        /// <summary>
        /// Builds the Opening Range by tracking the high/low from session open
        /// until OrbStartTime + OrbRangeMinutes. Resets each new session day.
        /// </summary>
        private void BuildOrbRange()
        {
            DateTime now         = Server.Time;
            DateTime sessionDate = now.Date;

            // New session day — reset ORB state
            if (sessionDate != _lastSessionDate)
            {
                _orbHigh        = double.MinValue;
                _orbLow         = double.MaxValue;
                _orbRangeSet    = false;
                _tradedToday    = false;
                _lastSessionDate = sessionDate;

                DateTime orbStart = new DateTime(sessionDate.Year, sessionDate.Month, sessionDate.Day,
                                                 OrbStartHour, OrbStartMinute, 0);
                _orbRangeEnd = orbStart.AddMinutes(OrbRangeMinutes);

                Print($"[ORB] New session. Range window: {orbStart:HH:mm}–{_orbRangeEnd:HH:mm} CET");
            }

            // Inside the range-building window — track high/low
            DateTime orbWindowStart = new DateTime(sessionDate.Year, sessionDate.Month, sessionDate.Day,
                                                   OrbStartHour, OrbStartMinute, 0);

            if (now >= orbWindowStart && now < _orbRangeEnd)
            {
                _orbHigh = Math.Max(_orbHigh, Bars.HighPrices.Last(1));
                _orbLow  = Math.Min(_orbLow,  Bars.LowPrices.Last(1));
                _orbRangeSet = false; // still building
            }
            else if (now >= _orbRangeEnd && !_orbRangeSet
                     && _orbHigh > double.MinValue && _orbLow < double.MaxValue)
            {
                // Range is now finalised
                _orbRangeSet = true;
                Print($"[ORB] Range locked: High={_orbHigh:F2}, Low={_orbLow:F2}, " +
                      $"Width={((_orbHigh - _orbLow) / Symbol.PipSize):F1} pips");
            }
        }

        // ══════════════════════════════════════════════════════════════
        // MODULE 3 — ORB ENTRY EVALUATION
        // ══════════════════════════════════════════════════════════════

        private void EvaluateOrbEntry()
        {
            if (!_orbRangeSet) return;
            if (_tradedToday)  return;

            DateTime now         = Server.Time;
            DateTime entryDeadline = new DateTime(now.Date.Year, now.Date.Month, now.Date.Day,
                                                  OrbEntryDeadlineHour, 0, 0);
            if (now >= entryDeadline)
            {
                Print($"[ORB] Entry deadline {OrbEntryDeadlineHour}:00 passed — no new entry today.");
                return;
            }

            // ── Regime gate ──────────────────────────────────────────
            if (!IsTrendingRegime()) return;

            // ── ORB quality filter: range must be meaningful ─────────
            double atrNow = _atr.Result.Last(1);
            if (OrbQualityAtrMult > 0 && (_orbHigh - _orbLow) < atrNow * OrbQualityAtrMult)
            {
                Print($"[ORB] Range too narrow ({(_orbHigh - _orbLow):F1} pts < ATR*{OrbQualityAtrMult} = {atrNow * OrbQualityAtrMult:F1}) — skip");
                return;
            }

            // ── EMA200 trend filter ──────────────────────────────────
            double ema200    = _ema200.Result.Last(1);
            double lastClose = Bars.ClosePrices.Last(1);
            bool   bullBias  = lastClose > ema200;
            bool   bearBias  = lastClose < ema200;

            // ── PDI/NDI directional confirmation ─────────────────────
            double pdi = _adxDmi.DIPlus.Last(1);
            double ndi = _adxDmi.DIMinus.Last(1);
            bool   pdiLong  = pdi > ndi;    // PDI above NDI → bullish momentum
            bool   pdiShort = ndi > pdi;    // NDI above PDI → bearish momentum

            // ── Breakout detection (5M bar-close outside range) ──────
            bool breakoutUp   = lastClose > _orbHigh && bullBias && pdiLong;
            bool breakoutDown = lastClose < _orbLow  && bearBias && pdiShort;

            if (!breakoutUp && !breakoutDown) return;

            // ── Compute stop distances ───────────────────────────────
            double atrValue  = _atr.Result.Last(1);
            double atrStop   = atrValue * SlAtrMultiplier;

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

            double atrStopPips   = atrStop / Symbol.PipSize;
            double stopPips      = Math.Min(rangeStop, atrStopPips); // tighter of the two

            if (stopPips <= 0)
            {
                Print($"[ORB] Invalid stop distance ({stopPips:F1} pips) — skipping");
                return;
            }

            // ── Position sizing ──────────────────────────────────────
            double volume = CalculateVolume(stopPips);
            if (volume <= 0)
            {
                Print("[ORB] Volume calculation returned 0 — skipping");
                return;
            }

            // ── Execute — Execution Engine-en keresztül ──────────────
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

                Print($"[ORB] {direction} entry routed via Execution Engine | " +
                      $"SL={stopPips:F1}p (range={rangeStop:F1}p, ATR={atrStopPips:F1}p) | " +
                      $"Vol={volume} | EMA200={ema200:F2}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        // MODULE 2b — LONDON ORB SESSION (8:00–8:30 UTC, optional)
        // ════════════════���═════════════════════════════════════════════

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
                      $"(width={(_londonOrbHigh-_londonOrbLow):F1} pts)");
            }
        }

        private void EvaluateLondonOrbEntry()
        {
            if (!_londonOrbRangeSet || _londonTradedToday || _tradedToday) return;

            DateTime nowUtc    = Server.Time.ToUniversalTime();
            DateTime closeUtc  = new DateTime(nowUtc.Date.Year, nowUtc.Date.Month, nowUtc.Date.Day,
                                              LondonSessionCloseHour, 0, 0, DateTimeKind.Utc);
            if (nowUtc < _londonOrbRangeEnd || nowUtc >= closeUtc) return;

            if (!IsTrendingRegime()) return;

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
            double stopPips    = Math.Min(rangeStop, atrStopPips);
            if (stopPips <= 0) return;

            TradeType dir = buLong ? TradeType.Buy : TradeType.Sell;
            double volume = CalculateVolume(stopPips);
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
                Print($"[London] {dir} | SL={stopPips:F1}p | Vol={volume} | PDI-NDI={diff:F1}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        // MODULE 4 — EXIT ENGINE
        // ══════════════════════════════════════════════════════════════

        private void ManageOpenPositions()
        {
            foreach (var pos in Positions.FindAll(Label, SymbolName))
            {
                double currentPips = pos.TradeType == TradeType.Buy
                    ? (Symbol.Bid - pos.EntryPrice) / Symbol.PipSize
                    : (pos.EntryPrice - Symbol.Ask) / Symbol.PipSize;

                // ══ LÉPÉS 1: Partial Close #1 ═══════════════════════
                // Feltétel: profit >= PartialCloseRTrigger × kezdeti SL
                // Akció: pozíció PartialClosePct%-ának zárása
                if (EnablePartialClose && !_partialCloseDone
                    && currentPips >= _initialSlPips * PartialCloseRTrigger)
                {
                    double closeVolume = Symbol.NormalizeVolumeInUnits(
                        pos.VolumeInUnits * PartialClosePct / 100.0);

                    if (closeVolume > 0 && closeVolume < pos.VolumeInUnits)
                    {
                        ClosePosition(pos, closeVolume);
                        _partialCloseDone = true;
                        Print($"[Exit] Partial Close #1: {PartialClosePct}% zárva " +
                              $"({closeVolume} unit) @ {currentPips:F1}R | " +
                              $"Maradék: {pos.VolumeInUnits - closeVolume} unit");
                    }
                }

                // ══ LÉPÉS 2: Partial Close #2 ═══════════════════════
                // Feltétel: partial #1 már megtörtént + profit >= SecondPartialCloseRTrigger × SL
                // Akció: maradék pozíció SecondPartialClosePct%-ának zárása
                if (EnableSecondPartialClose && _partialCloseDone && !_secondPartialDone
                    && currentPips >= _initialSlPips * SecondPartialCloseRTrigger)
                {
                    double closeVolume2 = Symbol.NormalizeVolumeInUnits(
                        pos.VolumeInUnits * SecondPartialClosePct / 100.0);

                    if (closeVolume2 > 0 && closeVolume2 < pos.VolumeInUnits)
                    {
                        ClosePosition(pos, closeVolume2);
                        _secondPartialDone = true;
                        Print($"[Exit] Partial Close #2: {SecondPartialClosePct}% zárva " +
                              $"({closeVolume2} unit) @ {currentPips:F1}R | " +
                              $"Maradék: {pos.VolumeInUnits - closeVolume2} unit");
                    }
                }

                // ══ LÉPÉS 3: Break-Even ══════════════════════════════
                // Aktivál: az 1. partial close után SL = entry árra mozgatva
                // Logika: ha már zártunk részlegesen, a maradék pozíció
                // kockázatmentes — a SL-t entry árra visszük
                if (!_breakEvenSet && _partialCloseDone)
                {
                    ModifyPosition(pos, pos.EntryPrice, pos.TakeProfit);
                    _breakEvenSet = true;
                    Print($"[Exit] Break-even set @ {pos.EntryPrice:F2} " +
                          $"(automatikus partial close után)");
                }
                else if (!_breakEvenSet
                         && currentPips >= _initialSlPips * BreakEvenRMultiple)
                {
                    // Break-even partial close nélkül is aktiválhat (ha disabled)
                    ModifyPosition(pos, pos.EntryPrice, pos.TakeProfit);
                    _breakEvenSet = true;
                    Print($"[Exit] Break-even set @ {pos.EntryPrice:F2} ({BreakEvenRMultiple}R)");
                }

                // ══ LÉPÉS 4: Donchian Decay Exit ════════════════════
                // A maradék pozíció Donchian 10 törésére zárul
                // (csak break-even után aktív, hogy ne záruljunk túl korán)
                if (_breakEvenSet)
                {
                    double donchianHigh = GetDonchianHigh(DonchianExitPeriod);
                    double donchianLow  = GetDonchianLow(DonchianExitPeriod);

                    bool decayExitLong  = pos.TradeType == TradeType.Buy
                                          && Bars.ClosePrices.Last(1) < donchianLow;
                    bool decayExitShort = pos.TradeType == TradeType.Sell
                                          && Bars.ClosePrices.Last(1) > donchianHigh;

                    if (decayExitLong || decayExitShort)
                    {
                        double exitPnl = pos.NetProfit;
                        ClosePosition(pos);
                        RecordTradeOutcome(pos);
                        Print($"[Exit] Donchian decay exit — " +
                              $"{pos.TradeType} closed @ " +
                              $"{(pos.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask):F2} | " +
                              $"P&L: {exitPnl:F2}");
                    }
                }
                else
                {
                    // Break-even előtt is figyeljük a Donchian-t —
                    // ha a piac visszafordul SL szint alá, a SL elvégzi a munkát.
                    // Donchian exit itt szándékosan inaktív: túl korai zárást okozna.
                }
            }
        }

        private void ManageTrailingStop()
        {
            double atrValue = _atr.Result.Last(1);
            if (atrValue <= 0) return;

            double trailDistancePips = atrValue * TrailingAtrMultiplier / Symbol.PipSize;

            foreach (var pos in Positions.FindAll(Label, SymbolName))
            {
                if (!_breakEvenSet) continue; // only trail after break-even is set

                if (pos.TradeType == TradeType.Buy)
                {
                    double newSl = Symbol.Bid - trailDistancePips * Symbol.PipSize;
                    if (pos.StopLoss == null || newSl > pos.StopLoss.Value)
                        ModifyPosition(pos, newSl, pos.TakeProfit);
                }
                else
                {
                    double newSl = Symbol.Ask + trailDistancePips * Symbol.PipSize;
                    if (pos.StopLoss == null || newSl < pos.StopLoss.Value)
                        ModifyPosition(pos, newSl, pos.TakeProfit);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        // MODULE 5 — FTMO RISK MANAGER
        // ══════════════════════════════════════════════════════════════

        private bool IsEntryAllowed()
        {
            // ── Cooldown after loss ──────────────────────────────────
            if (_cooldownBarsRemaining > 0)
            {
                _cooldownBarsRemaining--;
                Print($"[Risk] Cooldown active — {_cooldownBarsRemaining} bars remaining");
                return false;
            }

            // ── News Filter ──────────────────────────────────────────────
            if (EnableNewsFilter && IsNewsBlocked())
            {
                Print($"[NewsFilter] Entry blocked — news window active at {Server.Time:HH:mm} CET");
                return false;
            }

            // ── Spread filter ────────────────────────────────────────
            double spreadPips = Symbol.Spread / Symbol.PipSize;
            if (spreadPips > MaxSpreadPips)
            {
                Print($"[Risk] Spread too wide: {spreadPips:F1}p (max {MaxSpreadPips}p)");
                return false;
            }

            // ── Daily drawdown check — FTMO Swing szabály ───────────
            // FTMO: a napi veszteség = nap eleji balance - jelenlegi equity (lebegővel együtt)
            // Referencia: nap eleji balance (nem equity), mert a Swing DD balance-alapú
            double dailyDD = (_dailyStartBalance - Account.Equity) / _dailyStartBalance * 100.0;
            if (dailyDD >= MaxDailyDrawdownPct)
            {
                Print($"[Risk] DAILY DD CAP: {dailyDD:F2}% ≥ {MaxDailyDrawdownPct}% " +
                      $"(Day start: {_dailyStartBalance:F2}, Equity: {Account.Equity:F2}) — no entries today");
                return false;
            }

            // ── Total drawdown — FTMO Swing szabály ─────────────────
            // FTMO: a max DD = challenge kezdő egyenleg - jelenlegi equity
            // NEM a futó peak-től számolják, hanem az EREDETI challenge balance-tól!
            double totalDDFromChallenge = (_challengeStartBal - Account.Equity) / _challengeStartBal * 100.0;
            if (totalDDFromChallenge >= MaxTotalDrawdownPct)
            {
                Print($"[Risk] TOTAL DD CAP (FTMO): {totalDDFromChallenge:F2}% ≥ {MaxTotalDrawdownPct}% " +
                      $"(Challenge start: {_challengeStartBal:F2}, Equity: {Account.Equity:F2}) — STOPPING");
                CloseAllPositions("FTMO total DD cap reached");
                Stop();
                return false;
            }

            // ── Másodlagos: peak-alapú DD monitor (belső védelmi réteg) ──
            double totalDDFromPeak = (_peakBalance - Account.Equity) / _peakBalance * 100.0;
            if (totalDDFromPeak >= MaxTotalDrawdownPct * 0.85)
            {
                Print($"[Risk] PEAK DD WARNING: {totalDDFromPeak:F2}% — közel a limithez, " +
                      $"peak: {_peakBalance:F2}, equity: {Account.Equity:F2}");
                // Figyelmeztetés, nem hard stop — de logolva van
            }

            // ── No existing open position ────────────────────────────
            if (Positions.FindAll(Label, SymbolName).Length > 0) return false;

            return true;
        }

        // ══════════════════════════════════════════════════════════════
        // MODULE 6 — NEWS FILTER (Hibrid: statikus + egyedi ablakok)
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Visszaadja, hogy az aktuális Server.Time benne van-e egy tiltott hírablakon.
        /// Statikus ablakok: FOMC szerdák (19:00–22:00 CET), NFP első péntekek (13:00–16:00 CET).
        /// Dinamikus ablakok: felhasználó által megadott egyedi dátum+idő pontok ±buffer perccel.
        /// </summary>
        private bool IsNewsBlocked()
        {
            DateTime now = Server.Time; // CET (robot TimeZone = CET)

            // ── Statikus: FOMC — szerdánként 19:00–22:00 CET ────────
            if (BlockFomcWednesdays && now.DayOfWeek == DayOfWeek.Wednesday)
            {
                var fomcStart = new DateTime(now.Year, now.Month, now.Day, 19, 0, 0);
                var fomcEnd   = new DateTime(now.Year, now.Month, now.Day, 22, 0, 0);
                if (now >= fomcStart && now <= fomcEnd)
                {
                    Print($"[NewsFilter] FOMC szerda ablak aktív ({now:HH:mm} CET)");
                    return true;
                }
            }

            // ── Statikus: NFP — minden hónap első péntekje 13:00–16:00 CET ──
            if (BlockNfpFirstFridays && now.DayOfWeek == DayOfWeek.Friday && IsFirstFridayOfMonth(now))
            {
                var nfpStart = new DateTime(now.Year, now.Month, now.Day, 13, 0, 0);
                var nfpEnd   = new DateTime(now.Year, now.Month, now.Day, 16, 0, 0);
                if (now >= nfpStart && now <= nfpEnd)
                {
                    Print($"[NewsFilter] NFP első péntek ablak aktív ({now:HH:mm} CET)");
                    return true;
                }
            }

            // ── Statikus: US CPI — minden hónap 2. csütörtökje 13:00–16:00 CET ──
            if (BlockCpiSecondThursdays && now.DayOfWeek == DayOfWeek.Thursday && IsSecondThursdayOfMonth(now))
            {
                var cpiStart = new DateTime(now.Year, now.Month, now.Day, 13, 0, 0);
                var cpiEnd   = new DateTime(now.Year, now.Month, now.Day, 16, 0, 0);
                if (now >= cpiStart && now <= cpiEnd)
                {
                    Print($"[NewsFilter] CPI második csütörtök ablak aktív ({now:HH:mm} CET)");
                    return true;
                }
            }

            // ── Dinamikus: egyedi tiltott időablakok ─────────────────
            foreach (var blockTime in _customBlockTimes)
            {
                var windowStart = blockTime.AddMinutes(-NewsBufferBeforeMin);
                var windowEnd   = blockTime.AddMinutes(NewsBufferAfterMin);
                if (now >= windowStart && now <= windowEnd)
                {
                    Print($"[NewsFilter] Egyedi ablak aktív: {blockTime:yyyy-MM-dd HH:mm} " +
                          $"±{NewsBufferBeforeMin}/{NewsBufferAfterMin} perc");
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Meghatározza, hogy az adott dátum az adott hónap első péntekje-e.
        /// </summary>
        private bool IsFirstFridayOfMonth(DateTime date)
        {
            return date.Day <= 7;
        }

        /// <summary>
        /// Meghatározza, hogy az adott dátum az adott hónap második csütörtökje-e.
        /// </summary>
        private bool IsSecondThursdayOfMonth(DateTime date)
        {
            int thursdayCount = 0;
            for (int d = 1; d <= date.Day; d++)
            {
                if (new DateTime(date.Year, date.Month, d).DayOfWeek == DayOfWeek.Thursday)
                    thursdayCount++;
            }
            return thursdayCount == 2;
        }

        /// <summary>
        /// Parszálja a CustomBlock1-5 paramétereket DateTime listává.
        /// Elfogadott formátum: "yyyy-MM-dd HH:mm" (CET). Üres string = kihagyva.
        /// </summary>
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
                    Print($"[NewsFilter] Custom block regisztrálva: {parsed:yyyy-MM-dd HH:mm} CET " +
                          $"(ablak: -{NewsBufferBeforeMin}/+{NewsBufferAfterMin} perc)");
                }
                else
                {
                    Print($"[NewsFilter] FIGYELEM: Hibás formátum, kihagyva: '{raw}' " +
                          $"(elvárt: 'yyyy-MM-dd HH:mm')");
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        // MODULE 7 — RANGE ENGINE (VWAP ±2SD + RSI(7) + BB visszazárás)
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// VWAP intraday értékének folyamatos frissítése minden zárt bar-on.
        /// Session-alapú: naponta nullázódik. Variance alapú SD sáv számítással.
        /// </summary>
        private void UpdateVwap()
        {
            DateTime now = Server.Time;

            // Napi reset
            if (now.Date != _vwapSessionDate)
            {
                _vwapSumPV      = 0;
                _vwapSumV       = 0;
                _vwapSumPV2     = 0;
                _vwapBarCount   = 0;
                _vwapSessionDate = now.Date;
            }

            double typicalPrice = (Bars.HighPrices.Last(1) + Bars.LowPrices.Last(1) + Bars.ClosePrices.Last(1)) / 3.0;
            double vol          = Bars.TickVolumes.Last(1);

            if (vol <= 0) return;

            _vwapSumPV  += typicalPrice * vol;
            _vwapSumV   += vol;
            _vwapSumPV2 += typicalPrice * typicalPrice * vol;
            _vwapBarCount++;

            _vwapValue = _vwapSumPV / _vwapSumV;
        }

        /// <summary>
        /// VWAP standard deviation sáv kiszámítása.
        /// Visszaad: (vwap, vwap + n*SD, vwap - n*SD)
        /// </summary>
        private (double vwap, double upper, double lower) GetVwapBands(double multiplier)
        {
            if (_vwapSumV <= 0 || _vwapBarCount < 5)
                return (_vwapValue, _vwapValue, _vwapValue);

            double variance = (_vwapSumPV2 / _vwapSumV) - (_vwapValue * _vwapValue);
            double sd       = variance > 0 ? Math.Sqrt(variance) : 0;

            return (_vwapValue,
                    _vwapValue + multiplier * sd,
                    _vwapValue - multiplier * sd);
        }

        /// <summary>
        /// Range Engine entry logika.
        /// Feltételek:
        ///   - Regime: CI > RangeChoppinessMin AND ADX < RangeAdxMax
        ///   - Long: price volt VWAP - 2SD alatt, majd visszazárt fölé + RSI < RsiOversold + BB visszazárás
        ///   - Short: price volt VWAP + 2SD felett, majd visszazárt alá + RSI > RsiOverbought + BB visszazárás
        ///   - Max 1 range trade naponta (kisebb size, védelmi jelleg)
        /// </summary>
        private void EvaluateRangeEntry()
        {
            if (_rangeTradeToday) return;
            if (_vwapBarCount < 10) return; // VWAP-hoz legalább 10 bar kell

            // ── Range rezsim gate ────────────────────────────────────
            double ci  = CalculateChoppiness(ChoppinessPeriod);
            double adx = _adxDmi.ADX.Last(1);

            if (ci < RangeChoppinessMin || adx > RangeAdxMax)
            {
                // Nem konszolidációs rezsim — Range Engine nem lép be
                return;
            }

            var (vwap, vwapUpper, vwapLower) = GetVwapBands(VwapSdBand);

            double lastClose = Bars.ClosePrices.Last(1);
            double prevClose = Bars.ClosePrices.Last(2);
            double rsi       = _rsiRange.Result.Last(1);
            double bbUpper   = _bbRange.Top.Last(1);
            double bbLower   = _bbRange.Bottom.Last(1);

            bool longSignal  = false;
            bool shortSignal = false;

            // Long: előző gyertya VWAP lower band alatt zárult, aktuális visszazárt fölé
            // + RSI oversold területről + BB visszazárás (előző kívül, aktuális belül)
            if (prevClose < vwapLower
                && lastClose > vwapLower
                && rsi < RsiOversold
                && prevClose < bbLower
                && lastClose > bbLower)
            {
                longSignal = true;
            }

            // Short: előző gyertya VWAP upper band felett zárult, aktuális visszazárt alá
            // + RSI overbought területről + BB visszazárás
            if (prevClose > vwapUpper
                && lastClose < vwapUpper
                && rsi > RsiOverbought
                && prevClose > bbUpper
                && lastClose < bbUpper)
            {
                shortSignal = true;
            }

            if (!longSignal && !shortSignal) return;

            TradeType direction = longSignal ? TradeType.Buy : TradeType.Sell;

            // Stop: 1.5x ATR (range trade-nél konzervatívabb)
            double atrStop   = _atr.Result.Last(1) * SlAtrMultiplier / Symbol.PipSize;
            double stopPips  = atrStop;

            if (stopPips <= 0) return;

            // Range trade kisebb mérettel (RangeRiskPercent)
            double riskSave  = RiskPercent;
            // Átmenetileg módosítjuk a volume kalkulációhoz
            double volume = CalculateVolumeWithRisk(stopPips, RangeRiskPercent);
            if (volume <= 0) return;

            // Execution Engine-en keresztül
            var result = ExecuteEntryOrder(direction, volume, stopPips, "NAS100_ORB_RANGE");

            if (result)
            {
                _rangeTradeToday    = true;
                _lastTradeIsRange   = true;
                _initialSlPips      = stopPips;
                _breakEvenSet       = false;
                _partialCloseDone   = false;
                _secondPartialDone  = false;

                Print($"[Range] {direction} entry | VWAP: {vwap:F2} | " +
                      $"Band: {vwapLower:F2}–{vwapUpper:F2} | " +
                      $"RSI: {rsi:F1} | CI: {ci:F1} | ADX: {adx:F1}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        // MODULE 8 — EXECUTION ENGINE
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Egységes belépési pont minden trade-hez.
        /// Limit order módban: a jelzés irányába offset-tel limit-et ad le.
        /// Market order módban: azonnali végrehajtás slippage ellenőrzéssel.
        /// Spread circuit breaker: ha a spread meghaladja a SpreadCircuitBreakerPips-t,
        /// a belépés elmarad (nem csak log, hanem teljes blokk).
        /// </summary>
        private bool ExecuteEntryOrder(TradeType direction, double volume, double stopPips, string label)
        {
            // ── Spread circuit breaker ───────────────────────────────
            double spreadPips = Symbol.Spread / Symbol.PipSize;
            if (spreadPips > SpreadCircuitBreakerPips)
            {
                Print($"[Exec] SPREAD CIRCUIT BREAKER: {spreadPips:F1}p > {SpreadCircuitBreakerPips}p — entry blokkolt");
                return false;
            }

            int attempts = 0;
            int maxAttempts = Math.Max(1, ExecutionRetryCount + 1);

            while (attempts < maxAttempts)
            {
                attempts++;

                if (UseLimitOrders)
                {
                    // Limit order: ORB breakoutnál a gyertya close árától offset-tel
                    // vissza a range irányába helyezzük — jobb átlagtöltés
                    double limitPrice = direction == TradeType.Buy
                        ? Symbol.Ask - LimitOrderOffsetPips * Symbol.PipSize
                        : Symbol.Bid + LimitOrderOffsetPips * Symbol.PipSize;

                    var orderResult = PlaceLimitOrder(
                        direction, SymbolName, volume, limitPrice,
                        label, stopPips, null);

                    if (orderResult.IsSuccessful)
                    {
                        _limitOrderActive        = true;
                        _limitOrderBarsRemaining = LimitOrderExpiryBars;
                        Print($"[Exec] Limit {direction} @ {limitPrice:F2} | " +
                              $"SL: {stopPips:F1}p | Spread: {spreadPips:F1}p | Attempt: {attempts}");
                        return true;
                    }
                    else
                    {
                        Print($"[Exec] Limit order fail (attempt {attempts}): {orderResult.Error}");
                    }
                }
                else
                {
                    // Market order slippage ellenőrzéssel
                    double preBid = Symbol.Bid;
                    double preAsk = Symbol.Ask;

                    var orderResult = ExecuteMarketOrder(
                        direction, SymbolName, volume, label, stopPips, null);

                    if (orderResult.IsSuccessful)
                    {
                        double fillPrice   = orderResult.Position.EntryPrice;
                        double slippagePips = direction == TradeType.Buy
                            ? (fillPrice - preAsk) / Symbol.PipSize
                            : (preBid - fillPrice) / Symbol.PipSize;

                        if (slippagePips > MaxSlippagePips)
                        {
                            Print($"[Exec] SLIPPAGE ALERT: {slippagePips:F1}p > {MaxSlippagePips}p — pozíció azonnal zárva");
                            ClosePosition(orderResult.Position);
                            return false;
                        }

                        Print($"[Exec] Market {direction} fill @ {fillPrice:F2} | " +
                              $"Slippage: {slippagePips:F1}p | Spread: {spreadPips:F1}p");
                        _entryPrice      = fillPrice;
                        _entryRiskAmount = Account.Balance * RiskPercent / 100.0;
                        _logTradeSeq++;
                        if (EnableLogger) LogTradeOpen(orderResult.Position, slippagePips, spreadPips);
                        return true;
                    }
                    else
                    {
                        Print($"[Exec] Market order fail (attempt {attempts}): {orderResult.Error}");
                    }
                }

                // no sleep between retries — Thread.Sleep blocks cTrader's execution thread
            }

            Print($"[Exec] Minden kísérlet sikertelen ({maxAttempts}x) — entry elmarad");
            return false;
        }

        /// <summary>
        /// Limit order lejárat kezelése. Ha az order ExpiryBars-on belül
        /// nem töltődik ki, törli és loggolja.
        /// </summary>
        private void ManageLimitOrderExpiry()
        {
            if (!_limitOrderActive) return;

            _limitOrderBarsRemaining--;

            if (_limitOrderBarsRemaining <= 0)
            {
                // Töröljük az összes függő limit ordert
                foreach (var order in PendingOrders)
                {
                    if (order.Label == Label || order.Label == "NAS100_ORB_RANGE")
                    {
                        CancelPendingOrder(order);
                        Print($"[Exec] Limit order lejárt és törölve: {order.TradeType} {order.VolumeInUnits} @ {order.TargetPrice:F2}");
                    }
                }
                _limitOrderActive = false;
            }
        }

        // ══════════════════════════════════════════════════════════════
        // MODULE 9 — ANALYTICS
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Trade eredményének rögzítése az Analytics modulban.
        /// Minden pozíciózárásnál hívandó a RecordTradeOutcome() mellett.
        /// </summary>
        private void RecordAnalytics(Position pos)
        {
            if (!EnableAnalytics) return;

            bool isWin   = pos.NetProfit > 0;
            double pnl   = pos.NetProfit;
            _totalTrades++;

            // Rolling win rate
            _rollingResults.Enqueue(isWin);
            if (_rollingResults.Count > AnalyticsRollingWindow)
                _rollingResults.Dequeue();

            // Modul-szintű szétválasztás
            if (_lastTradeIsRange)
            {
                if (isWin) _winRange++; else _lossRange++;
                _totalPnlRange += pnl;
            }
            else
            {
                if (isWin) _winTrend++; else _lossTrend++;
                _totalPnlTrend += pnl;
            }

            // Rolling win rate számítás
            int   wins           = 0;
            foreach (bool w in _rollingResults) if (w) wins++;
            double rollingWinRate = _rollingResults.Count > 0
                ? (double)wins / _rollingResults.Count * 100.0 : 0;

            // Expectancy számítás
            int   totalTrend = _winTrend + _lossTrend;
            int   totalRange = _winRange + _lossRange;
            double trendWR   = totalTrend > 0 ? (double)_winTrend / totalTrend * 100.0 : 0;
            double rangeWR   = totalRange > 0 ? (double)_winRange / totalRange * 100.0 : 0;

            Print($"[Analytics] Trade #{_totalTrades} | {(isWin ? "WIN" : "LOSS")} {pnl:F2} | " +
                  $"Module: {(_lastTradeIsRange ? "RANGE" : "TREND")} | " +
                  $"Rolling WR ({AnalyticsRollingWindow}): {rollingWinRate:F1}%");

            Print($"[Analytics] Trend: {_winTrend}W/{_lossTrend}L ({trendWR:F1}%) P&L:{_totalPnlTrend:F2} | " +
                  $"Range: {_winRange}W/{_lossRange}L ({rangeWR:F1}%) P&L:{_totalPnlRange:F2}");

            // Figyelmeztető jelzés: ha a Range Engine nettó negatív 10+ trade után
            if (totalRange >= 10 && _totalPnlRange < 0)
            {
                Print($"[Analytics] ⚠ FIGYELEM: Range Engine nettó negatív ({_totalPnlRange:F2}) " +
                      $"{totalRange} trade után — fontold meg a kikapcsolást!");
            }
        }

        /// <summary>
        /// Napi Analytics összefoglaló — ResetDailyState-ben hívandó.
        /// </summary>
        private void LogDailyAnalytics()
        {
            if (!EnableAnalytics) return;

            double totalPnl = _totalPnlTrend + _totalPnlRange;
            Print($"[Analytics Daily] Összes trade: {_totalTrades} | " +
                  $"Trend P&L: {_totalPnlTrend:F2} | Range P&L: {_totalPnlRange:F2} | " +
                  $"Összes P&L: {totalPnl:F2}");
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
            _rangeTradeToday   = false;
            _breakEvenSet      = false;
            _partialCloseDone  = false;
            _secondPartialDone = false;
            _limitOrderActive  = false;
            // London ORB daily reset
            _londonOrbHigh     = double.MinValue;
            _londonOrbLow      = double.MaxValue;
            _londonOrbRangeSet = false;
            _londonTradedToday = false;

            // Napi DD státusz logolása challenge kezdő egyenlegtől
            double usedDDPct = (_challengeStartBal - Account.Balance) / _challengeStartBal * 100.0;

            LogDailyAnalytics();
            Print($"[Daily Reset] {_lastDayChecked:yyyy-MM-dd} | " +
                  $"Balance: {_dailyStartBalance:F2} | " +
                  $"Challenge DD used: {usedDDPct:F2}% / {MaxTotalDrawdownPct}% | " +
                  $"Peak: {_peakBalance:F2}");
        }

        /// <summary>
        /// Peak balance folyamatos frissítése minden OnBar()-ban.
        /// Csak realizált balance-t követ (nem equity), mert az FTMO
        /// a trailing DD-t closed trade-ek után számolja Swing számlán.
        /// </summary>
        private void UpdatePeakBalance()
        {
            if (Account.Balance > _peakBalance)
            {
                _peakBalance = Account.Balance;
                Print($"[Peak] Új balance csúcs: {_peakBalance:F2}");
            }
        }

        /// <summary>
        /// Teljes DD státusz riport — hívásonként logolható diagnosztikához.
        /// </summary>
        private void LogDdStatus()
        {
            double dailyDD          = (_dailyStartBalance - Account.Equity) / _dailyStartBalance * 100.0;
            double totalFromChallenge = (_challengeStartBal - Account.Equity) / _challengeStartBal * 100.0;
            double totalFromPeak    = (_peakBalance - Account.Equity) / _peakBalance * 100.0;

            Print($"[DD Status] Napi: {dailyDD:F2}%/{MaxDailyDrawdownPct}% | " +
                  $"Challenge total: {totalFromChallenge:F2}%/{MaxTotalDrawdownPct}% | " +
                  $"Peak DD: {totalFromPeak:F2}% | " +
                  $"Equity: {Account.Equity:F2} | Balance: {Account.Balance:F2}");
        }

        private bool IsSessionEnd()
        {
            return Server.Time.Hour >= SessionEndHour;
        }

        private void CloseAllPositions(string reason)
        {
            foreach (var pos in Positions.FindAll(Label, SymbolName))
            {
                ClosePosition(pos);
                Print($"[Risk] Position closed — reason: {reason}");
            }
        }

        private void RecordTradeOutcome(Position pos)
        {
            _lastTradeWasLoss = pos.NetProfit < 0;
            if (_lastTradeWasLoss)
                _cooldownBarsRemaining = PostLossCooldownBars;

            // Analytics rögzítése
            RecordAnalytics(pos);
            // Logger: kötés részletek
            if (EnableLogger) LogTradeClose(pos);
            // Reset trade type flag
            _lastTradeIsRange = false;
        }

        private double CalculateVolume(double stopPips)
        {
            double adxNow = _adxDmi.ADX.Last(1);
            double ciNow  = CalculateChoppiness(ChoppinessPeriod);
            bool   gradeA = adxNow > GradeAAdxMin && ciNow < GradeACiMax;
            double risk   = gradeA ? GradeARiskPercent : RiskPercent;
            if (gradeA)
                Print($"[Risk] Grade-A setup (ADX={adxNow:F1}>{GradeAAdxMin}, CI={ciNow:F1}<{GradeACiMax}) — using {risk}% risk");
            return CalculateVolumeWithRisk(stopPips, risk);
        }

        private double CalculateVolumeWithRisk(double stopPips, double riskPct)
        {
            if (stopPips <= 0 || Symbol.PipValue <= 0) return 0;

            double riskAmount = Account.Balance * (riskPct / 100.0);
            double volume     = riskAmount / (stopPips * Symbol.PipValue);
            volume            = Symbol.NormalizeVolumeInUnits(volume);
            volume            = Math.Min(volume, MaxPositionSizeUnits);   // safety cap
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

        // ══════════════════════════════════════════════════════════════
        // MODULE 10 — LOGGER
        // ══════════════════════════════════════════════════════════════

        private static readonly string Sep = new string('═', 72);
        private static readonly string Sep2 = new string('─', 72);

        /// <summary>
        /// Indulási banner — robot verzió, platform, számla, időpont.
        /// </summary>
        private void LogStartupBanner()
        {
            Print(Sep);
            Print($"[LOGGER] NAS100 ORB + Regime Engine — INDULÁS");
            Print($"[LOGGER] Időpont    : {_botStartTime:yyyy-MM-dd HH:mm:ss} CET");
            Print($"[LOGGER] Számla     : {Account.Number} | {Account.BrokerName}");
            Print($"[LOGGER] Balance    : {Account.Balance:F2} {Account.Currency}");
            Print($"[LOGGER] Challenge  : {_challengeStartBal:F2} {Account.Currency}");
            Print($"[LOGGER] Instrument : {SymbolName} | TF: {TimeFrame}");
            Print($"[LOGGER] Szerver idő: {Server.Time:yyyy-MM-dd HH:mm:ss}");
            Print(Sep);
        }

        /// <summary>
        /// Teljes paraméter dump — minden modul összes beállítása.
        /// Induláskor logolódik, így a backteszt log tartalmazza a konfigurációt.
        /// </summary>
        private void LogParameterDump()
        {
            Print("[PARAMS] ── Regime Engine ──────────────────────────────────────");
            Print($"[PARAMS]  ADX Period          = {AdxPeriod}");
            Print($"[PARAMS]  ADX Threshold       = {AdxThreshold}");
            Print($"[PARAMS]  Choppiness Period   = {ChoppinessPeriod}");
            Print($"[PARAMS]  Choppiness Threshold= {ChoppinessThreshold}");

            Print("[PARAMS] ── ORB / Trend Engine ─────────────────────────────────");
            Print($"[PARAMS]  ORB Start           = {OrbStartHour:D2}:{OrbStartMinute:D2} CET");
            Print($"[PARAMS]  ORB Range Minutes   = {OrbRangeMinutes} perc");
            Print($"[PARAMS]  ORB Entry Deadline  = {OrbEntryDeadlineHour:D2}:00 CET");
            Print($"[PARAMS]  EMA200 Period       = {Ema200Period}");
            Print($"[PARAMS]  ATR Period          = {AtrPeriod}");
            Print($"[PARAMS]  SL ATR Multiplier   = {SlAtrMultiplier}x");

            Print("[PARAMS] ── Exit Engine ─────────────────────────────────────────");
            Print($"[PARAMS]  Donchian Exit Period= {DonchianExitPeriod}");
            Print($"[PARAMS]  ATR Trail Mult      = {TrailingAtrMultiplier}x");
            Print($"[PARAMS]  Break-Even R        = {BreakEvenRMultiple}R");
            Print($"[PARAMS]  Partial Close       = {EnablePartialClose} @ {PartialCloseRTrigger}R, {PartialClosePct}%");
            Print($"[PARAMS]  2nd Partial         = {EnableSecondPartialClose} @ {SecondPartialCloseRTrigger}R, {SecondPartialClosePct}%");

            Print("[PARAMS] ── FTMO Risk Manager ──────────────────────────────────");
            Print($"[PARAMS]  Challenge Balance   = {ChallengeStartBalance:F2}");
            Print($"[PARAMS]  Risk / Trade        = {RiskPercent}%");
            Print($"[PARAMS]  Max Position Size   = {MaxPositionSizeUnits} units");
            Print($"[PARAMS]  Max Daily DD        = {MaxDailyDrawdownPct}%  (FTMO limit: 5%)");
            Print($"[PARAMS]  Max Total DD        = {MaxTotalDrawdownPct}%  (FTMO limit: 10%)");
            Print($"[PARAMS]  Max Spread          = {MaxSpreadPips} pips");
            Print($"[PARAMS]  Post-Loss Cooldown  = {PostLossCooldownBars} bars");
            Print($"[PARAMS]  Session End         = {SessionEndHour:D2}:00 CET | CloseAtEnd: {CloseAtSessionEnd}");

            Print("[PARAMS] ── News Filter ─────────────────────────────────────────");
            Print($"[PARAMS]  Enabled             = {EnableNewsFilter}");
            Print($"[PARAMS]  Buffer Before       = {NewsBufferBeforeMin} perc");
            Print($"[PARAMS]  Buffer After        = {NewsBufferAfterMin} perc");
            Print($"[PARAMS]  Block FOMC Szerda   = {BlockFomcWednesdays}");
            Print($"[PARAMS]  Block NFP Péntek    = {BlockNfpFirstFridays}");
            Print($"[PARAMS]  Block CPI 2. Csütört= {BlockCpiSecondThursdays}");
            Print($"[PARAMS]  Custom Blocks       = {_customBlockTimes.Count} db");

            Print("[PARAMS] ── Range Engine ────────────────────────────────────────");
            Print($"[PARAMS]  Enabled             = {EnableRangeEngine}");
            Print($"[PARAMS]  CI Min (range)      = {RangeChoppinessMin}");
            Print($"[PARAMS]  ADX Max (range)     = {RangeAdxMax}");
            Print($"[PARAMS]  VWAP SD Band        = ±{VwapSdBand}SD");
            Print($"[PARAMS]  RSI Period          = {RangeRsiPeriod}");
            Print($"[PARAMS]  RSI Oversold/OB     = {RsiOversold} / {RsiOverbought}");
            Print($"[PARAMS]  BB Period/StdDev    = {RangeBbPeriod} / {RangeBbStdDev}");
            Print($"[PARAMS]  Range Risk %        = {RangeRiskPercent}%");

            Print("[PARAMS] ── Execution Engine ───────────────────────────────────");
            Print($"[PARAMS]  Use Limit Orders    = {UseLimitOrders}");
            Print($"[PARAMS]  Limit Offset        = {LimitOrderOffsetPips} pips");
            Print($"[PARAMS]  Limit Expiry        = {LimitOrderExpiryBars} bars");
            Print($"[PARAMS]  Max Slippage        = {MaxSlippagePips} pips");
            Print($"[PARAMS]  Spread CB           = {SpreadCircuitBreakerPips} pips");
            Print($"[PARAMS]  Retry Count         = {ExecutionRetryCount}x");

            Print("[PARAMS] ── Analytics ──────────────────────────────────────────");
            Print($"[PARAMS]  Enable Analytics    = {EnableAnalytics}");
            Print($"[PARAMS]  Rolling Window      = {AnalyticsRollingWindow} trade");

            Print("[PARAMS] ── Logger ─────────────────────────────────────────────");
            Print($"[PARAMS]  Heartbeat Interval  = {HeartbeatIntervalBars} bars");
            Print($"[PARAMS]  Trade Detail Level  = {TradeDetailLevel}");
            Print(Sep);
        }

        /// <summary>
        /// Heartbeat — minden N barban jelzi, hogy a robot él.
        /// Level 3: részletes indikátor állapot is logolódik.
        /// </summary>
        private void LogHeartbeat()
        {
            _heartbeatBarCounter++;
            if (_heartbeatBarCounter < HeartbeatIntervalBars) return;
            _heartbeatBarCounter = 0;

            double dailyDD    = (_dailyStartBalance - Account.Equity) / _dailyStartBalance * 100.0;
            double totalDD    = (_challengeStartBal - Account.Equity) / _challengeStartBal * 100.0;
            double spreadPips = Symbol.Spread / Symbol.PipSize;
            int    openPos    = Positions.FindAll(Label, SymbolName).Length;

            Print(Sep2);
            Print($"[HEARTBEAT] {Server.Time:yyyy-MM-dd HH:mm} CET | " +
                  $"Uptime: {(Server.Time - _botStartTime).TotalHours:F1}h | " +
                  $"Nyitott pos: {openPos}");
            Print($"[HEARTBEAT] Balance: {Account.Balance:F2} | Equity: {Account.Equity:F2} | " +
                  $"Napi DD: {dailyDD:F2}% | Total DD: {totalDD:F2}%");
            Print($"[HEARTBEAT] Spread: {spreadPips:F1}p | " +
                  $"ORB range kész: {_orbRangeSet} | " +
                  $"Ma kereskedett: {_tradedToday}");

            if (TradeDetailLevel >= 3)
            {
                double adx        = _adxDmi.ADX.Last(1);
                double ci         = CalculateChoppiness(ChoppinessPeriod);
                double ema200     = _ema200.Result.Last(1);
                double atr        = _atr.Result.Last(1);
                string rezsim     = (adx > AdxThreshold && ci < ChoppinessThreshold) ? "TRENDING" : "CHOPPY";

                Print($"[HEARTBEAT] ADX: {adx:F1} | CI: {ci:F1} | Rezsim: {rezsim}");
                Print($"[HEARTBEAT] EMA200: {ema200:F2} | ATR: {atr:F2} | " +
                      $"VWAP: {_vwapValue:F2}");
                if (_orbRangeSet)
                    Print($"[HEARTBEAT] ORB High: {_orbHigh:F2} | ORB Low: {_orbLow:F2} | " +
                          $"Szélesség: {((_orbHigh - _orbLow) / Symbol.PipSize):F1} pips");
            }

            // Nyitott pozíciók állapota
            foreach (var pos in Positions.FindAll(Label, SymbolName))
            {
                double floatingPnl = pos.NetProfit;
                double pips        = pos.TradeType == TradeType.Buy
                    ? (Symbol.Bid - pos.EntryPrice) / Symbol.PipSize
                    : (pos.EntryPrice - Symbol.Ask) / Symbol.PipSize;
                Print($"[HEARTBEAT] POS #{pos.Id} {pos.TradeType} {pos.VolumeInUnits} | " +
                      $"Entry: {pos.EntryPrice:F2} | Pips: {pips:F1} | " +
                      $"Floating P&L: {floatingPnl:F2} | SL: {pos.StopLoss:F2}");
            }

            Print(Sep2);
        }

        /// <summary>
        /// Trade nyitás részletes naplója. Detail Level 1-3 szerint skálázódik.
        /// </summary>
        private void LogTradeOpen(Position pos, double slippagePips, double spreadAtFill)
        {
            string module = _lastTradeIsRange ? "RANGE" : "TREND/ORB";
            double adx    = _adxDmi.ADX.Last(1);
            double ci     = CalculateChoppiness(ChoppinessPeriod);
            double atr    = _atr.Result.Last(1);
            double ema200 = _ema200.Result.Last(1);

            Print(Sep);
            Print($"[TRADE OPEN #{_logTradeSeq}] ════ {pos.TradeType} | {module} ════");
            Print($"[TRADE OPEN #{_logTradeSeq}]  Időpont    : {Server.Time:yyyy-MM-dd HH:mm:ss} CET");
            Print($"[TRADE OPEN #{_logTradeSeq}]  Belépési ár: {pos.EntryPrice:F5}");
            Print($"[TRADE OPEN #{_logTradeSeq}]  Stop Loss  : {pos.StopLoss:F5} ({_initialSlPips:F1} pips)");
            Print($"[TRADE OPEN #{_logTradeSeq}]  Volumen    : {pos.VolumeInUnits} unit");
            Print($"[TRADE OPEN #{_logTradeSeq}]  Kockázat   : {Account.Balance * RiskPercent / 100.0:F2} {Account.Currency}");

            if (TradeDetailLevel >= 2)
            {
                Print($"[TRADE OPEN #{_logTradeSeq}]  ── Piaci kontextus ──");
                Print($"[TRADE OPEN #{_logTradeSeq}]  ADX        : {adx:F1}  (limit: >{AdxThreshold})");
                Print($"[TRADE OPEN #{_logTradeSeq}]  Choppiness : {ci:F1}  (limit: <{ChoppinessThreshold})");
                Print($"[TRADE OPEN #{_logTradeSeq}]  EMA200     : {ema200:F2}");
                Print($"[TRADE OPEN #{_logTradeSeq}]  ATR(14)    : {atr:F2}");
                Print($"[TRADE OPEN #{_logTradeSeq}]  Spread     : {spreadAtFill:F1} pips");
                Print($"[TRADE OPEN #{_logTradeSeq}]  Slippage   : {slippagePips:F1} pips");

                if (!_lastTradeIsRange && _orbRangeSet)
                {
                    Print($"[TRADE OPEN #{_logTradeSeq}]  ── ORB adatok ──");
                    Print($"[TRADE OPEN #{_logTradeSeq}]  ORB High   : {_orbHigh:F2}");
                    Print($"[TRADE OPEN #{_logTradeSeq}]  ORB Low    : {_orbLow:F2}");
                    Print($"[TRADE OPEN #{_logTradeSeq}]  ORB Széles.: {((_orbHigh - _orbLow) / Symbol.PipSize):F1} pips");
                }

                if (_lastTradeIsRange)
                {
                    var (vwap, upper, lower) = GetVwapBands(VwapSdBand);
                    Print($"[TRADE OPEN #{_logTradeSeq}]  ── VWAP adatok ──");
                    Print($"[TRADE OPEN #{_logTradeSeq}]  VWAP       : {vwap:F2}");
                    Print($"[TRADE OPEN #{_logTradeSeq}]  VWAP+{VwapSdBand}SD  : {upper:F2}");
                    Print($"[TRADE OPEN #{_logTradeSeq}]  VWAP-{VwapSdBand}SD  : {lower:F2}");
                    Print($"[TRADE OPEN #{_logTradeSeq}]  RSI({RangeRsiPeriod})    : {_rsiRange.Result.Last(1):F1}");
                }
            }

            if (TradeDetailLevel >= 3)
            {
                double dailyDD = (_dailyStartBalance - Account.Equity) / _dailyStartBalance * 100.0;
                double totalDD = (_challengeStartBal - Account.Equity) / _challengeStartBal * 100.0;

                Print($"[TRADE OPEN #{_logTradeSeq}]  ── DD állapot entry-kor ──");
                Print($"[TRADE OPEN #{_logTradeSeq}]  Napi DD    : {dailyDD:F2}% / {MaxDailyDrawdownPct}%");
                Print($"[TRADE OPEN #{_logTradeSeq}]  Total DD   : {totalDD:F2}% / {MaxTotalDrawdownPct}%");
                Print($"[TRADE OPEN #{_logTradeSeq}]  Balance    : {Account.Balance:F2}");
                Print($"[TRADE OPEN #{_logTradeSeq}]  Equity     : {Account.Equity:F2}");
                Print($"[TRADE OPEN #{_logTradeSeq}]  Peak Bal.  : {_peakBalance:F2}");
                Print($"[TRADE OPEN #{_logTradeSeq}]  Limit Order: {UseLimitOrders}");
                Print($"[TRADE OPEN #{_logTradeSeq}]  News Block : {(EnableNewsFilter ? IsNewsBlocked().ToString() : "disabled")}");
            }

            Print(Sep);
        }

        /// <summary>
        /// Trade zárás részletes naplója — minden kötésnél teljes eredményelemzés.
        /// </summary>
        private void LogTradeClose(Position pos)
        {
            // Compute pips from GrossProfit (avoids using stale live bid/ask on a closed position)
            double pipsResult = (pos.VolumeInUnits > 0 && Symbol.PipValue > 0)
                ? pos.GrossProfit / (pos.VolumeInUnits * Symbol.PipValue)
                : 0;

            // R-multiple: use captured entry risk amount for accuracy; fall back to current balance
            double riskBase  = _entryRiskAmount > 0 ? _entryRiskAmount : Account.Balance * RiskPercent / 100.0;
            double rMultiple = riskBase > 0 ? pos.NetProfit / riskBase : 0;
            double dailyDD    = (_dailyStartBalance - Account.Equity) / _dailyStartBalance * 100.0;
            double totalDD    = (_challengeStartBal - Account.Equity) / _challengeStartBal * 100.0;
            bool   isWin      = pos.NetProfit > 0;
            string module     = _lastTradeIsRange ? "RANGE" : "TREND/ORB";

            Print(Sep);
            Print($"[TRADE CLOSE #{_logTradeSeq}] ════ {(isWin ? "✔ WIN" : "✘ LOSS")} | {pos.TradeType} | {module} ════");
            Print($"[TRADE CLOSE #{_logTradeSeq}]  Időpont    : {Server.Time:yyyy-MM-dd HH:mm:ss} CET");
            Print($"[TRADE CLOSE #{_logTradeSeq}]  Belépés    : {pos.EntryPrice:F5}");
            Print($"[TRADE CLOSE #{_logTradeSeq}]  Zárás      : {(pos.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask):F5}");
            Print($"[TRADE CLOSE #{_logTradeSeq}]  Pips       : {pipsResult:F1}");
            Print($"[TRADE CLOSE #{_logTradeSeq}]  R-szorzó   : {rMultiple:F2}R");
            Print($"[TRADE CLOSE #{_logTradeSeq}]  Nettó P&L  : {pos.NetProfit:F2} {Account.Currency}");
            Print($"[TRADE CLOSE #{_logTradeSeq}]  Komisszió  : {pos.Commissions:F2} | Swap: {pos.Swap:F2}");

            if (TradeDetailLevel >= 2)
            {
                Print($"[TRADE CLOSE #{_logTradeSeq}]  ── Trade életút ──");
                Print($"[TRADE CLOSE #{_logTradeSeq}]  Partial #1 : {_partialCloseDone}");
                Print($"[TRADE CLOSE #{_logTradeSeq}]  Partial #2 : {_secondPartialDone}");
                Print($"[TRADE CLOSE #{_logTradeSeq}]  Break-Even : {_breakEvenSet}");
                Print($"[TRADE CLOSE #{_logTradeSeq}]  SL volt    : {pos.StopLoss:F5}");
                Print($"[TRADE CLOSE #{_logTradeSeq}]  Volumen    : {pos.VolumeInUnits} unit");
            }

            if (TradeDetailLevel >= 3)
            {
                int   totalT  = _winTrend + _lossTrend + _winRange + _lossRange;
                int   totalW  = _winTrend + _winRange;
                double overallWR = totalT > 0 ? (double)totalW / totalT * 100.0 : 0;

                Print($"[TRADE CLOSE #{_logTradeSeq}]  ── Kumulált statisztikák ──");
                Print($"[TRADE CLOSE #{_logTradeSeq}]  Összes trade  : {totalT}");
                Print($"[TRADE CLOSE #{_logTradeSeq}]  Win Rate      : {overallWR:F1}%");
                Print($"[TRADE CLOSE #{_logTradeSeq}]  Trend W/L     : {_winTrend}/{_lossTrend} | P&L: {_totalPnlTrend:F2}");
                Print($"[TRADE CLOSE #{_logTradeSeq}]  Range W/L     : {_winRange}/{_lossRange} | P&L: {_totalPnlRange:F2}");
                Print($"[TRADE CLOSE #{_logTradeSeq}]  ── Post-close DD ──");
                Print($"[TRADE CLOSE #{_logTradeSeq}]  Napi DD       : {dailyDD:F2}% / {MaxDailyDrawdownPct}%");
                Print($"[TRADE CLOSE #{_logTradeSeq}]  Total DD      : {totalDD:F2}% / {MaxTotalDrawdownPct}%");
                Print($"[TRADE CLOSE #{_logTradeSeq}]  Balance       : {Account.Balance:F2}");
                Print($"[TRADE CLOSE #{_logTradeSeq}]  Peak Balance  : {_peakBalance:F2}");
            }

            Print(Sep);
        }

        /// <summary>
        /// Leállási összefoglaló — teljes session statisztika.
        /// </summary>
        private void LogShutdownSummary()
        {
            double totalPnl     = _totalPnlTrend + _totalPnlRange;
            int    totalTrades  = _winTrend + _lossTrend + _winRange + _lossRange;
            int    totalWins    = _winTrend + _winRange;
            double overallWR    = totalTrades > 0 ? (double)totalWins / totalTrades * 100.0 : 0;
            double runHours     = (Server.Time - _botStartTime).TotalHours;
            double finalDD      = (_challengeStartBal - Account.Balance) / _challengeStartBal * 100.0;

            Print(Sep);
            Print($"[LOGGER] NAS100 ORB + Regime Engine — LEÁLLÁS ÖSSZEFOGLALÓ");
            Print($"[LOGGER]  Futási idő    : {runHours:F1} óra");
            Print($"[LOGGER]  Indult        : {_botStartTime:yyyy-MM-dd HH:mm} CET");
            Print($"[LOGGER]  Leállt        : {Server.Time:yyyy-MM-dd HH:mm} CET");
            Print(Sep2);
            Print($"[LOGGER]  Nyitó balance : {_initialBalance:F2} {Account.Currency}");
            Print($"[LOGGER]  Záró balance  : {Account.Balance:F2} {Account.Currency}");
            Print($"[LOGGER]  Nettó P&L     : {totalPnl:F2} {Account.Currency}");
            Print($"[LOGGER]  Challenge DD  : {finalDD:F2}% / {MaxTotalDrawdownPct}%");
            Print($"[LOGGER]  Peak balance  : {_peakBalance:F2} {Account.Currency}");
            Print(Sep2);
            Print($"[LOGGER]  Összes trade  : {totalTrades}");
            Print($"[LOGGER]  Win Rate      : {overallWR:F1}%  ({totalWins}W / {totalTrades - totalWins}L)");
            Print($"[LOGGER]  Trend trades  : {_winTrend + _lossTrend}  | W/L: {_winTrend}/{_lossTrend} | P&L: {_totalPnlTrend:F2}");
            Print($"[LOGGER]  Range trades  : {_winRange + _lossRange}  | W/L: {_winRange}/{_lossRange} | P&L: {_totalPnlRange:F2}");
            Print(Sep);
        }

    }
}
