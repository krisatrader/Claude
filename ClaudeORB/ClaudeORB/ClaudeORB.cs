using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    /// <summary>
    /// ORB-Prop v4 — Opening Range Breakout cBot index CFD-ekre (FTMO US100 / US30 / US500).
    ///
    /// Verziótörténet (a felülvizsgálati megjegyzések beépítve):
    ///   (1) PIP paraméter: a küszöbök és a napló mértékegysége; alapért. FTMO US100 (1 pont = 1.0).
    ///       A pozícióméretezés és az SL/TP elhelyezés ÁRFOLYAM-alapú / natív pip → minden brókeren helyes.
    ///   (2) Teljes drawdown a KEZDŐ egyenleghez mérve (FTMO normál 10% Max Loss logikája), nem trailing.
    ///   (3) Teljes DD elérésekor a robot SZÁNDÉKOSAN véglegesen leáll (Stop) — lásd a kódban.
    ///   (4) A napi DD 00:00 UTC-kor vált; a stratégia ablakára nincs hatása — lásd a kódban.
    ///   (5) Amerikai félnapos ünnepeket nem kezel külön — lásd a kódban.
    ///
    /// Korábbi alapok: auto US szakasz + DST, relatív volumen (Stocks in Play) szűrő, hír-szűrő,
    /// részletes naplózás, fix töredékes kockázat, nincs overnight kitettség.
    /// </summary>
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class ClaudeORB : Robot
    {
        private const string Label = "ORB-Prop";

        public enum LogVerbosity { Off, Trades, Verbose, Debug }

        // ── Paraméterek: Instrumentum ────────────────────────────
        // (1) PipSize = 1 "pip" ára árfolyam-egységben. FTMO US100 alapért.: 1 index pont = 1.0.
        //     0 = Auto (a szimbólum saját PipSize-át használja — FTMO cTrader-en ez a helyes érték).
        //     FONTOS: ez CSAK a küszöbök (OR szélesség, spread, trail lépés) és a napló mértékegysége.
        //     A volumenszámítás és az SL/TP elhelyezés árfolyam-alapú ill. natív pip, ezért a pip-
        //     definíciótól függetlenül mindig helyes marad. A START log kiírja az érvényes értéket.
        [Parameter("Pip Size (0=Auto)", Group = "Instrument", DefaultValue = 1.0, MinValue = 0.0, MaxValue = 1000.0)]
        public double PipSize { get; set; }

        // ── Paraméterek: Kockázat ────────────────────────────────
        [Parameter("Risk % per Trade", Group = "Risk", DefaultValue = 1.1, MinValue = 0.1, MaxValue = 3.0)]
        public double RiskPercent { get; set; }

        [Parameter("Max Daily DD %", Group = "Risk", DefaultValue = 4.0, MinValue = 1.0, MaxValue = 10.0)]
        public double MaxDailyDrawdownPct { get; set; }

        [Parameter("Max Total DD %", Group = "Risk", DefaultValue = 8.0, MinValue = 3.0, MaxValue = 20.0)]
        public double MaxTotalDrawdownPct { get; set; }

        [Parameter("Max Spread (pips)", Group = "Risk", DefaultValue = 3.0, MinValue = 0.5, MaxValue = 30.0)]
        public double MaxSpreadPips { get; set; }

        [Parameter("Max Trades / Day", Group = "Risk", DefaultValue = 1, MinValue = 1, MaxValue = 5)]
        public int MaxTradesPerDay { get; set; }

        // ── Paraméterek: Szakasz ─────────────────────────────────
        [Parameter("Auto US Session (DST)", Group = "Session", DefaultValue = true)]
        public bool AutoUsSession { get; set; }

        [Parameter("Manual Start Hour (UTC)", Group = "Session", DefaultValue = 14, MinValue = 0, MaxValue = 23)]
        public int SessionStartHour { get; set; }

        [Parameter("Manual Start Minute", Group = "Session", DefaultValue = 30, MinValue = 0, MaxValue = 59)]
        public int SessionStartMinute { get; set; }

        [Parameter("Manual Flatten Hour (UTC)", Group = "Session", DefaultValue = 20, MinValue = 0, MaxValue = 23)]
        public int FlattenHour { get; set; }

        [Parameter("Manual Flatten Minute", Group = "Session", DefaultValue = 55, MinValue = 0, MaxValue = 59)]
        public int FlattenMinute { get; set; }

        [Parameter("Flatten Buffer (min)", Group = "Session", DefaultValue = 5, MinValue = 0, MaxValue = 60)]
        public int FlattenBufferMinutes { get; set; }

        [Parameter("Opening Range (min)", Group = "Session", DefaultValue = 5, MinValue = 1, MaxValue = 60)]
        public int OpeningRangeMinutes { get; set; }

        [Parameter("Trade Window (min)", Group = "Session", DefaultValue = 120, MinValue = 5, MaxValue = 390)]
        public int TradeWindowMinutes { get; set; }

        // ── Paraméterek: Hír-szűrő ───────────────────────────────
        [Parameter("Use News Filter", Group = "News", DefaultValue = false)]
        public bool UseNewsFilter { get; set; }

        // Pontosvesszővel elvalasztott UTC idopontok, pl: "2026-01-29 19:00; 2026-02-12 13:30"
        [Parameter("News Events (UTC list)", Group = "News", DefaultValue = "")]
        public string NewsEventsRaw { get; set; }

        [Parameter("News Buffer Before (min)", Group = "News", DefaultValue = 15, MinValue = 0, MaxValue = 240)]
        public int NewsBufferBefore { get; set; }

        [Parameter("News Buffer After (min)", Group = "News", DefaultValue = 15, MinValue = 0, MaxValue = 240)]
        public int NewsBufferAfter { get; set; }

        [Parameter("Block 10:00 ET Data", Group = "News", DefaultValue = true)]
        public bool Block1000EtData { get; set; }

        [Parameter("Close Before News", Group = "News", DefaultValue = false)]
        public bool CloseBeforeNews { get; set; }

        // ── Paraméterek: Relatív volumen (Stocks in Play) ────────
        [Parameter("Use RVOL Filter", Group = "Volume", DefaultValue = true)]
        public bool UseRelVolFilter { get; set; }

        [Parameter("RVOL Multiplier", Group = "Volume", DefaultValue = 0.5, MinValue = 0.1, MaxValue = 5.0)]
        public double RelVolMultiplier { get; set; }

        [Parameter("RVOL Lookback (days)", Group = "Volume", DefaultValue = 20, MinValue = 3, MaxValue = 100)]
        public int RelVolLookbackDays { get; set; }

        [Parameter("RVOL Min Samples", Group = "Volume", DefaultValue = 10, MinValue = 1, MaxValue = 100)]
        public int RelVolMinSamples { get; set; }

        // ── Paraméterek: Stratégia ───────────────────────────────
        [Parameter("Directional Filter", Group = "Strategy", DefaultValue = true)]
        public bool DirectionalFilter { get; set; }

        [Parameter("Min OR Width (pips)", Group = "Strategy", DefaultValue = 30.0, MinValue = 0.0, MaxValue = 500.0)]
        public double MinOrWidthPips { get; set; }

        [Parameter("Max OR Width (pips)", Group = "Strategy", DefaultValue = 150.0, MinValue = 0.0, MaxValue = 5000.0)]
        public double MaxOrWidthPips { get; set; }

        [Parameter("TP (R multiple)", Group = "Strategy", DefaultValue = 12.0, MinValue = 1.0, MaxValue = 15.0)]
        public double TpRMultiple { get; set; }

        [Parameter("Breakeven at (R)", Group = "Strategy", DefaultValue = 1.5, MinValue = 0.0, MaxValue = 5.0)]
        public double BreakevenR { get; set; }

        [Parameter("Trail after BE (ATR x)", Group = "Strategy", DefaultValue = 0.0, MinValue = 0.0, MaxValue = 10.0)]
        public double TrailAtrMult { get; set; }

        [Parameter("ATR Period", Group = "Strategy", DefaultValue = 14, MinValue = 5, MaxValue = 50)]
        public int AtrPeriod { get; set; }

        [Parameter("Trail Min Step (pips)", Group = "Strategy", DefaultValue = 2.0, MinValue = 0.0, MaxValue = 100.0)]
        public double TrailMinStepPips { get; set; }

        // ── Paraméterek: Naplózás ────────────────────────────────
        [Parameter("Log Verbosity", Group = "Logging", DefaultValue = LogVerbosity.Debug)]
        public LogVerbosity LogLevel { get; set; }

        // ── Indikátorok ──────────────────────────────────────────
        private AverageTrueRange _atr;

        // ── Állapot: drawdown ────────────────────────────────────
        private double _dailyStartBalance;
        private double _initialBalance;   // (2) a teljes DD ehhez mérve, statikusan
        private DateTime _lastDayChecked;

        // ── Állapot: szakasz / nyitó range ───────────────────────
        private DateTime _sessionDate;
        private double _orHigh, _orLow, _orOpen, _orClose, _orVolume;
        private bool _orComplete;
        private bool _orHasData;
        private bool _orDirectionUp;
        private int _tradesToday;
        private readonly Queue<double> _orVolHistory = new Queue<double>();
        private readonly HashSet<string> _loggedReasonsToday = new HashSet<string>();
        private bool _summaryLogged;

        // ── Állapot: aktív pozíció kezelése ──────────────────────
        private double _entryPrice;
        private double _initialRiskPips;   // natív pip (Symbol.PipSize) — a BE/trail/R ezzel konzisztens
        private bool _beDone;

        // ── Állapot: hírek ───────────────────────────────────────
        private readonly List<DateTime> _newsEvents = new List<DateTime>();

        // (1) Érvényes pip ára árfolyam-egységben (küszöbökhöz és naplóhoz).
        private double EffectivePip()
        {
            return PipSize > 0 ? PipSize : Symbol.PipSize;
        }

        protected override void OnStart()
        {
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);
            _dailyStartBalance = Account.Balance;
            _initialBalance = Account.Balance;
            _lastDayChecked = Server.Time.Date;
            _sessionDate = DateTime.MinValue;
            ResetOpeningRange();
            ParseNewsEvents();
            Positions.Closed += OnPositionClosed;

            Log(LogVerbosity.Trades, "START",
                $"ORB-Prop v4 | sym={SymbolName} tf={TimeFrame} bal={Account.Balance:F2} " +
                $"pip={EffectivePip():F2}(sym={Symbol.PipSize}) autoSession={AutoUsSession} " +
                $"news={UseNewsFilter}({_newsEvents.Count} esem.) rvol={UseRelVolFilter}");
        }

        protected override void OnStop()
        {
            Positions.Closed -= OnPositionClosed;
        }

        private void ParseNewsEvents()
        {
            _newsEvents.Clear();
            if (string.IsNullOrWhiteSpace(NewsEventsRaw)) return;

            foreach (var part in NewsEventsRaw.Split(';'))
            {
                string s = part.Trim();
                if (s.Length == 0) continue;
                if (DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
                    _newsEvents.Add(dt);
                else
                    Log(LogVerbosity.Trades, "CONFIG",
                        $"Hír-esemény értelmezhetetlen: '{s}' (várt formátum: yyyy-MM-dd HH:mm, UTC)");
            }
            Log(LogVerbosity.Trades, "CONFIG", $"{_newsEvents.Count} hír-esemény betöltve (UTC).");
        }

        private void ResetOpeningRange()
        {
            _orHigh = double.MinValue;
            _orLow = double.MaxValue;
            _orOpen = 0;
            _orClose = 0;
            _orVolume = 0;
            _orComplete = false;
            _orHasData = false;
            _orDirectionUp = false;
            _tradesToday = 0;
            _summaryLogged = false;
            _loggedReasonsToday.Clear();
        }

        // ── Naplózó segédfüggvény ────────────────────────────────
        private void Log(LogVerbosity level, string cat, string msg)
        {
            if (LogLevel >= level && LogLevel != LogVerbosity.Off)
                Print($"{Server.Time:yyyy-MM-dd HH:mm:ss} [{cat}] {msg}");
        }

        // Belépés-elmaradás logolása (naponta egyszer indokonként, hogy ne floodoljon)
        private void NoEntry(string code, string detail)
        {
            if (_loggedReasonsToday.Add(code))
                Log(LogVerbosity.Verbose, "NOENTRY", $"{code} | {detail}");
        }

        // ── US keleti idő DST-számítás ───────────────────────────
        private DateTime NthSunday(int year, int month, int n)
        {
            DateTime first = new DateTime(year, month, 1);
            int offset = (((int)DayOfWeek.Sunday - (int)first.DayOfWeek) + 7) % 7;
            return first.AddDays(offset + (n - 1) * 7);
        }

        private bool IsUsEastDst(DateTime utc)
        {
            int y = utc.Year;
            DateTime dstStart = NthSunday(y, 3, 2).AddHours(7);  // marcius 2. vas. 02:00 EST = 07:00 UTC
            DateTime dstEnd = NthSunday(y, 11, 1).AddHours(6);   // november 1. vas. 02:00 EDT = 06:00 UTC
            return utc >= dstStart && utc < dstEnd;
        }

        private void ComputeSessionTimes(out DateTime orStart, out DateTime orEnd,
                                         out DateTime tradeEnd, out DateTime flatten)
        {
            int startHour, startMin, closeHour, closeMin;
            if (AutoUsSession)
            {
                bool dst = IsUsEastDst(_sessionDate.AddHours(12));
                startHour = dst ? 13 : 14; startMin = 30;   // 09:30 ET
                closeHour = dst ? 20 : 21; closeMin = 0;     // 16:00 ET
                // (5) Amerikai félnapos ünnepeket (korai zárás 13:00 ET) NEM kezel külön: ilyenkor
                //     a pozíció a normál flatten időpontban zár, nem a korábbi tőzsdezárásnál. Ritka
                //     eset, és a stop egyébként véd; ha kell, a Manual időkkel kézzel felülírható.
            }
            else
            {
                startHour = SessionStartHour; startMin = SessionStartMinute;
                closeHour = FlattenHour; closeMin = FlattenMinute;
            }

            orStart = _sessionDate.AddHours(startHour).AddMinutes(startMin);
            orEnd = orStart.AddMinutes(OpeningRangeMinutes);
            tradeEnd = orStart.AddMinutes(TradeWindowMinutes);

            DateTime sessionClose = _sessionDate.AddHours(closeHour).AddMinutes(closeMin);
            flatten = AutoUsSession ? sessionClose.AddMinutes(-FlattenBufferMinutes) : sessionClose;
        }

        // ── Hír-ablak ellenőrzés ─────────────────────────────────
        private bool IsNewsBlackout(DateTime now, out string ev)
        {
            ev = "";
            foreach (var e in _newsEvents)
            {
                if (now >= e.AddMinutes(-NewsBufferBefore) && now <= e.AddMinutes(NewsBufferAfter))
                {
                    ev = e.ToString("yyyy-MM-dd HH:mm") + " UTC";
                    return true;
                }
            }
            if (Block1000EtData)
            {
                bool dst = IsUsEastDst(now);
                DateTime tenEt = now.Date.AddHours(dst ? 14 : 15); // 10:00 ET = 14:00/15:00 UTC
                if (now >= tenEt.AddMinutes(-NewsBufferBefore) && now <= tenEt.AddMinutes(NewsBufferAfter))
                {
                    ev = "10:00 ET adat";
                    return true;
                }
            }
            return false;
        }

        protected override void OnBar()
        {
            DateTime now = Server.Time;

            // (4) A napi DD-alap 00:00 UTC-kor nullázódik. Az FTMO prágai (közép-európai) éjfélkor
            //     nullázza a napi számlálót, de mivel a robot csak ~13:30–16:00 UTC között kereskedik,
            //     a két éjfél között nincs kötés, így a napi induló egyenleg gyakorlatilag azonos.
            if (now.Date != _lastDayChecked)
            {
                _dailyStartBalance = Account.Balance;
                _lastDayChecked = now.Date;
                Log(LogVerbosity.Trades, "DAY", $"Új nap. Napi induló egyenleg={_dailyStartBalance:F2}");
            }

            if (now.Date != _sessionDate)
            {
                if (_orHasData)
                {
                    _orVolHistory.Enqueue(_orVolume);
                    while (_orVolHistory.Count > RelVolLookbackDays) _orVolHistory.Dequeue();
                }
                _sessionDate = now.Date;
                ResetOpeningRange();
            }

            DateTime orStart, orEnd, tradeEnd, flatten;
            ComputeSessionTimes(out orStart, out orEnd, out tradeEnd, out flatten);

            // Opcionális: zárás közelgő hír előtt
            if (UseNewsFilter && CloseBeforeNews && Positions.FindAll(Label, SymbolName).Length > 0)
            {
                string evN;
                if (IsNewsBlackout(now, out evN))
                {
                    Log(LogVerbosity.Trades, "NEWS-CLOSE", $"Hír-ablak ({evN}) — pozíció zárása");
                    CloseAll();
                    return;
                }
            }

            // Szakaszzárás
            if (now >= flatten)
            {
                if (Positions.FindAll(Label, SymbolName).Length > 0)
                    Log(LogVerbosity.Trades, "FLATTEN", $"Szakaszzárás {flatten:HH:mm} — minden pozíció zárva");
                CloseAll();
                return;
            }

            DateTime barOpen = Bars.OpenTimes.Last(1);
            double barHigh = Bars.HighPrices.Last(1);
            double barLow = Bars.LowPrices.Last(1);
            double barOpenP = Bars.OpenPrices.Last(1);
            double barCloseP = Bars.ClosePrices.Last(1);
            double barVol = Bars.TickVolumes.Last(1);

            // Nyitó range felépítése
            if (barOpen >= orStart && barOpen < orEnd)
            {
                if (!_orHasData) { _orOpen = barOpenP; _orHasData = true; }
                _orHigh = Math.Max(_orHigh, barHigh);
                _orLow = Math.Min(_orLow, barLow);
                _orClose = barCloseP;
                _orVolume += barVol;
                return;
            }

            // Range lezárása + napi setup-összegzés logolása
            if (!_orComplete && _orHasData && now >= orEnd)
            {
                _orComplete = true;
                _orDirectionUp = _orClose >= _orOpen;
                if (!_summaryLogged)
                {
                    double w = (_orHigh - _orLow) / EffectivePip();
                    double relVol = _orVolHistory.Count > 0 && _orVolHistory.Average() > 0
                        ? _orVolume / _orVolHistory.Average() : 0;
                    Log(LogVerbosity.Verbose, "SETUP",
                        $"OR kész | H={_orHigh} L={_orLow} width={w:F1}p vol={_orVolume:F0} " +
                        $"relVol={relVol:F2} dir={(_orDirectionUp ? "UP" : "DOWN")} " +
                        $"window->{tradeEnd:HH:mm} flat->{flatten:HH:mm}");
                    _summaryLogged = true;
                }
            }

            if (!_orComplete) return;
            if (now >= tradeEnd)
            {
                NoEntry("WINDOW_CLOSED", $"kereskedési ablak lezárult {tradeEnd:HH:mm}-kor");
                return;
            }

            if (!CheckRiskGate()) return;
            if (_tradesToday >= MaxTradesPerDay) { NoEntry("MAX_TRADES", $"napi limit ({MaxTradesPerDay}) elérve"); return; }

            EvaluateEntry(now, barCloseP);
        }

        // ── Belépés kiértékelése részletes indoklással ───────────
        private void EvaluateEntry(DateTime now, double barCloseP)
        {
            bool brokeUp = barCloseP > _orHigh;
            bool brokeDown = barCloseP < _orLow;

            if (!brokeUp && !brokeDown)
            {
                Log(LogVerbosity.Debug, "WAIT",
                    $"nincs kitörés | close={barCloseP} OR[{_orLow}-{_orHigh}]");
                return;
            }

            TradeType dir = brokeUp ? TradeType.Buy : TradeType.Sell;

            // Irányszűrő
            if (DirectionalFilter && ((brokeUp && !_orDirectionUp) || (brokeDown && _orDirectionUp)))
            {
                NoEntry("DIR_FILTER",
                    $"kitörés {dir}, de OR irány={(_orDirectionUp ? "UP" : "DOWN")} — ellentétes");
                return;
            }

            // Hír-ablak
            if (UseNewsFilter)
            {
                string ev;
                if (IsNewsBlackout(now, out ev)) { NoEntry("NEWS", $"hír-ablak: {ev}"); return; }
            }

            // Relatív volumen
            if (UseRelVolFilter && _orVolHistory.Count >= RelVolMinSamples)
            {
                double avg = _orVolHistory.Average();
                double rel = avg > 0 ? _orVolume / avg : 0;
                if (rel < RelVolMultiplier)
                {
                    NoEntry("RVOL", $"relVol={rel:F2} < küszöb {RelVolMultiplier:F2} (átlag={avg:F0})");
                    return;
                }
            }
            else if (UseRelVolFilter)
            {
                Log(LogVerbosity.Debug, "RVOL", $"bemelegítés: csak {_orVolHistory.Count}/{RelVolMinSamples} minta");
            }

            // OR szélesség korlátok (a PIP paraméter mértékegységében)
            double width = (_orHigh - _orLow) / EffectivePip();
            if (width < MinOrWidthPips) { NoEntry("OR_TOO_NARROW", $"width={width:F1}p < {MinOrWidthPips}p"); return; }
            if (MaxOrWidthPips > 0 && width > MaxOrWidthPips) { NoEntry("OR_TOO_WIDE", $"width={width:F1}p > {MaxOrWidthPips}p"); return; }

            EnterTrade(dir);
        }

        // ── BELÉPÉS ──────────────────────────────────────────────
        private void EnterTrade(TradeType dir)
        {
            double entry = dir == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            double slPrice = dir == TradeType.Buy ? _orLow : _orHigh;
            double slDistance = Math.Abs(entry - slPrice);      // árfolyam-távolság
            if (slDistance <= 0) { NoEntry("BAD_SL", "érvénytelen SL táv (0)"); return; }

            // ExecuteMarketOrder NATÍV pipben kéri az SL/TP-t → mindig Symbol.PipSize-zal számolunk.
            double slPipsNative = slDistance / Symbol.PipSize;
            double tpPipsNative = slPipsNative * TpRMultiple;
            double slPipsDisplay = slDistance / EffectivePip(); // naplóhoz (PIP paraméter szerint)

            double volume = CalculateVolume(slDistance);
            if (volume <= 0) { NoEntry("ZERO_VOL", "számolt volumen 0 vagy minimum alatt"); return; }

            double riskMoney = Account.Balance * (RiskPercent / 100.0);

            var result = ExecuteMarketOrder(dir, SymbolName, volume, Label, slPipsNative, tpPipsNative);
            if (result.IsSuccessful)
            {
                _entryPrice = result.Position.EntryPrice;
                _initialRiskPips = slPipsNative; // natív pip — BE/trail/R ezzel konzisztens
                _beDone = false;
                _tradesToday++;
                Log(LogVerbosity.Trades, "ENTRY",
                    $"{dir} @ {_entryPrice} | SL={slPipsDisplay:F1}p TP={slPipsDisplay * TpRMultiple:F1}p ({TpRMultiple:F1}R) " +
                    $"vol={volume} risk={riskMoney:F2} ({RiskPercent}%) spread={Symbol.Spread / EffectivePip():F1}p");
            }
            else
            {
                Log(LogVerbosity.Trades, "ORDER-FAIL", $"{dir} sikertelen: {result.Error}");
            }
        }

        // ── POZÍCIÓKEZELÉS ───────────────────────────────────────
        protected override void OnTick()
        {
            var pos = Positions.FindAll(Label, SymbolName).FirstOrDefault();
            if (pos == null) return;
            if (_initialRiskPips <= 0) return;

            double rPips = _initialRiskPips;     // natív pip
            double profitPips = pos.Pips;        // natív pip

            if (!_beDone && BreakevenR > 0 && profitPips >= rPips * BreakevenR)
            {
                ModifyToBreakeven(pos);
                _beDone = true;
                Log(LogVerbosity.Trades, "BREAKEVEN",
                    $"SL nullára húzva @ {pos.EntryPrice} (profit={profitPips:F1}p = {profitPips / rPips:F2}R)");
            }

            if (_beDone && TrailAtrMult > 0)
                TrailByAtr(pos);
        }

        private void ModifyToBreakeven(Position pos)
        {
            double be = pos.EntryPrice;
            if (pos.TradeType == TradeType.Buy && (pos.StopLoss == null || pos.StopLoss < be))
                ModifyPosition(pos, be, pos.TakeProfit, ProtectionType.Absolute);
            else if (pos.TradeType == TradeType.Sell && (pos.StopLoss == null || pos.StopLoss > be))
                ModifyPosition(pos, be, pos.TakeProfit, ProtectionType.Absolute);
        }

        private void TrailByAtr(Position pos)
        {
            double atr = _atr.Result.Last(1);
            if (atr <= 0) return;
            double dist = atr * TrailAtrMult;
            double minStep = TrailMinStepPips * EffectivePip(); // minimális elmozdulás árfolyamban

            if (pos.TradeType == TradeType.Buy)
            {
                double newSl = Symbol.Bid - dist;
                if (pos.StopLoss == null || newSl - pos.StopLoss.Value >= minStep)
                {
                    ModifyPosition(pos, newSl, pos.TakeProfit, ProtectionType.Absolute);
                    Log(LogVerbosity.Debug, "TRAIL", $"BUY SL->{newSl:F2} (ATR={atr:F2} x{TrailAtrMult})");
                }
            }
            else
            {
                double newSl = Symbol.Ask + dist;
                if (pos.StopLoss == null || pos.StopLoss.Value - newSl >= minStep)
                {
                    ModifyPosition(pos, newSl, pos.TakeProfit, ProtectionType.Absolute);
                    Log(LogVerbosity.Debug, "TRAIL", $"SELL SL->{newSl:F2} (ATR={atr:F2} x{TrailAtrMult})");
                }
            }
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var p = args.Position;
            if (p.Label != Label || p.SymbolName != SymbolName) return;

            double rMultiple = _initialRiskPips > 0 ? p.Pips / _initialRiskPips : 0;
            Log(LogVerbosity.Trades, "EXIT",
                $"{p.TradeType} ok={args.Reason} | pips={p.Pips:F1} R={rMultiple:F2} " +
                $"grossPL={p.GrossProfit:F2} netPL={p.NetProfit:F2} comm={p.Commissions:F2} " +
                $"swap={p.Swap:F2} -> bal={Account.Balance:F2} eq={Account.Equity:F2}");

            _initialRiskPips = 0;
            _beDone = false;
        }

        // ── KOCKÁZAT ─────────────────────────────────────────────
        private bool CheckRiskGate()
        {
            // Napi DD: az aznapi induló egyenleghez mérve (FTMO napi limit logikája).
            double dailyDD = (_dailyStartBalance - Account.Equity) / _dailyStartBalance * 100.0;
            if (dailyDD >= MaxDailyDrawdownPct)
            {
                NoEntry("DAILY_DD", $"napi DD={dailyDD:F2}% ≥ {MaxDailyDrawdownPct}% — ma stop");
                return false;
            }

            // (2) Teljes DD a KEZDŐ egyenleghez mérve, statikusan — ez felel meg az FTMO normál
            //     kihívás 10%-os Max Loss szabályának (nem a csúcs-egyenlethez viszonyított trailing).
            double totalDD = (_initialBalance - Account.Equity) / _initialBalance * 100.0;
            if (totalDD >= MaxTotalDrawdownPct)
            {
                // (3) SZÁNDÉKOSAN véglegesen leállítja a robotot (Stop) — kemény vészfék a számla
                //     védelmére. Másnap NEM indul újra magától, manuális újraindítás szükséges.
                Log(LogVerbosity.Trades, "MAX-DD",
                    $"teljes DD={totalDD:F2}% ≥ {MaxTotalDrawdownPct}% (kezdő egyenleghez) — minden zárva, robot leáll");
                CloseAll();
                Stop();
                return false;
            }

            if (Positions.FindAll(Label, SymbolName).Length > 0) return false; // csendben: már van pozíció

            double spread = Symbol.Spread / EffectivePip();
            if (spread > MaxSpreadPips)
            {
                NoEntry("SPREAD", $"spread={spread:F1}p > {MaxSpreadPips}p");
                return false;
            }
            return true;
        }

        // Volumen: ÁRFOLYAM-alapú, pip-definíciótól független (mindig helyes a sizing).
        private double CalculateVolume(double stopDistancePrice)
        {
            if (stopDistancePrice <= 0 || Symbol.PipValue <= 0 || Symbol.PipSize <= 0) return 0;
            double riskAmount = Account.Balance * (RiskPercent / 100.0);
            double valuePerPrice = Symbol.PipValue / Symbol.PipSize; // számla-deviza / 1.0 árfolyam / 1 egység
            double rawVolume = riskAmount / (stopDistancePrice * valuePerPrice);
            double volume = Symbol.NormalizeVolumeInUnits(rawVolume, RoundingMode.Down);

            // Ha a normalizált volumen a szimbólum minimuma alatt van, inkább kihagyjuk:
            // a minimumra kerekítés túllépné a tervezett kockázatot (fontos prop-számlán).
            if (volume < Symbol.VolumeInUnitsMin) return 0;
            return Math.Min(volume, Symbol.VolumeInUnitsMax);
        }

        private void CloseAll()
        {
            foreach (var p in Positions.FindAll(Label, SymbolName))
                ClosePosition(p);
        }
    }
}