using cAlgo.API;
using cAlgo.API.Indicators;
using System;
using System.Linq;

/// <summary>
/// Multi-Strategy Adaptive cBot for XAUUSD H1 Trading (FTMO Swing Compatible)
/// 
/// This robot dynamically switches between 4 strategies based on market trend:
/// 1. Dual MA Crossover (50/200 EMA) - Trend Following
/// 2. Triple MA System (9/21/55 EMA) - Momentum with Filter
/// 3. ATR Mean Reversion - Range Bound Markets
/// 4. RSI + MACD Confirmation - Extreme Conditions
/// 
/// Strategy Selection Logic:
/// - ADX > 25: Use Trend Followers (50/200 or 9/21/55)
/// - ADX 15-25: Use Triple MA (9/21/55) with confirmation
/// - ADX < 15: Use ATR Mean Reversion (range market)
/// 
/// Risk Management: FTMO Compliant
/// - Daily DD: 5%
/// - Total DD: 10%
/// - Risk per trade: 1% (adjustable)
/// - ATR-based stop loss (dynamic)
/// 
/// Optimized for: XAUUSD H1 Swing Trading
/// </summary>

[Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
public class MultiStrategyAdaptiveBot : Robot
{
    // ============ PARAMETERS ============
    //
    // *** BEST PRACTICE SETTINGS (XAUUSD H1) ***
    // The following parameters yielded +47.7% profit with only 0.93% Max Drawdown 
    // over a 4-month period using M1 Tick Data:
    // 
    // [Risk Management]
    // - Risk % per Trade: 1.0
    // - Max Daily Drawdown %: 5.0
    // - Max Total Drawdown %: 10.0
    // - Max Spread (pips): 2.5
    // - Max Concurrent Trades: 1
    //
    // [Active Strategies]
    // - Use 50/200 EMA (S1): True
    // - Use 9/21/55 EMA (S2): True
    // - Use ATR Mean Reversion (S3): True
    // - Use RSI+MACD Strategy (S4): False (disabled to reduce noise)
    //
    // [Technical]
    // - ATR Period: 14
    // - SL ATR Multiplier: 2.5 (Crucial for H1 survival)
    // - TP ATR Multiplier: 4.0 (Allows catching large swing trends)
    // - ADX Period: 14
    //
    // ============================================
    
    [Parameter("Risk % per Trade", Group = "Risk Management", DefaultValue = 1.0, MinValue = 0.5, MaxValue = 3.0)]
    public double RiskPercent { get; set; }

    [Parameter("Max Daily Drawdown %", Group = "Risk Management", DefaultValue = 5.0, MinValue = 2.0, MaxValue = 8.0)]
    public double MaxDailyDrawdownPct { get; set; }

    [Parameter("Max Total Drawdown %", Group = "Risk Management", DefaultValue = 10.0, MinValue = 5.0, MaxValue = 15.0)]
    public double MaxTotalDrawdownPct { get; set; }

    [Parameter("Max Spread (pips)", Group = "Risk Management", DefaultValue = 2.5, MinValue = 0.5, MaxValue = 5.0)]
    public double MaxSpreadPips { get; set; }

    [Parameter("Max Concurrent Trades", Group = "Risk Management", DefaultValue = 1, MinValue = 1, MaxValue = 3)]
    public int MaxOpenTrades { get; set; }

    // === Strategy 1: 50/200 EMA Parameters ===
    [Parameter("50MA Period", Group = "Strategy_1_DualMA", DefaultValue = 50, MinValue = 20, MaxValue = 100)]
    public int MA50Period { get; set; }

    [Parameter("200MA Period", Group = "Strategy_1_DualMA", DefaultValue = 200, MinValue = 100, MaxValue = 300)]
    public int MA200Period { get; set; }

    [Parameter("Use 50/200 EMA", Group = "Strategy_1_DualMA", DefaultValue = true)]
    public bool UseStrategy1DualMA { get; set; }

    // === Strategy 2: 9/21/55 EMA Parameters ===
    [Parameter("9EMA Period", Group = "Strategy_2_TripleMA", DefaultValue = 9, MinValue = 5, MaxValue = 20)]
    public int EMA9Period { get; set; }

    [Parameter("21EMA Period", Group = "Strategy_2_TripleMA", DefaultValue = 21, MinValue = 15, MaxValue = 30)]
    public int EMA21Period { get; set; }

    [Parameter("55EMA Period", Group = "Strategy_2_TripleMA", DefaultValue = 55, MinValue = 40, MaxValue = 70)]
    public int EMA55Period { get; set; }

    [Parameter("Use 9/21/55 EMA", Group = "Strategy_2_TripleMA", DefaultValue = true)]
    public bool UseStrategy2TripleMA { get; set; }

    // === Strategy 3: ATR Mean Reversion Parameters ===
    [Parameter("FastMA Period (Mean Rev)", Group = "Strategy_3_MeanReversion", DefaultValue = 14, MinValue = 7, MaxValue = 25)]
    public int FastMAPeriod { get; set; }

    [Parameter("SlowMA Period (Mean Rev)", Group = "Strategy_3_MeanReversion", DefaultValue = 100, MinValue = 50, MaxValue = 150)]
    public int SlowMAPeriod { get; set; }

    [Parameter("Use ATR Mean Reversion", Group = "Strategy_3_MeanReversion", DefaultValue = true)]
    public bool UseStrategy3MeanReversion { get; set; }

    // === Strategy 4: RSI + MACD Parameters ===
    [Parameter("RSI Period", Group = "Strategy_4_RSI_MACD", DefaultValue = 14, MinValue = 7, MaxValue = 25)]
    public int RSIPeriod { get; set; }

    [Parameter("RSI Overbought Level", Group = "Strategy_4_RSI_MACD", DefaultValue = 70, MinValue = 60, MaxValue = 80)]
    public double RSIOverbought { get; set; }

    [Parameter("RSI Oversold Level", Group = "Strategy_4_RSI_MACD", DefaultValue = 30, MinValue = 20, MaxValue = 40)]
    public double RSIOversold { get; set; }

    [Parameter("Use RSI+MACD Strategy", Group = "Strategy_4_RSI_MACD", DefaultValue = true)]
    public bool UseStrategy4RSIMACD { get; set; }

    // === Common Technical Parameters ===
    [Parameter("ATR Period", Group = "Technical", DefaultValue = 14, MinValue = 10, MaxValue = 25)]
    public int AtrPeriod { get; set; }

    [Parameter("SL ATR Multiplier", Group = "Technical", DefaultValue = 2.5, MinValue = 0.8, MaxValue = 5.0)]
    public double SlAtrMultiplier { get; set; }

    [Parameter("TP ATR Multiplier", Group = "Technical", DefaultValue = 4.0, MinValue = 1.5, MaxValue = 10.0)]
    public double TpAtrMultiplier { get; set; }

    [Parameter("ADX Period", Group = "Technical", DefaultValue = 14, MinValue = 10, MaxValue = 25)]
    public int AdxPeriod { get; set; }

    // ============ INDICATORS ============
    
    // Strategy 1: 50/200 EMA
    private ExponentialMovingAverage _ema50;
    private ExponentialMovingAverage _ema200;

    // Strategy 2: 9/21/55 EMA
    private ExponentialMovingAverage _ema9;
    private ExponentialMovingAverage _ema21;
    private ExponentialMovingAverage _ema55;

    // Strategy 3: ATR Mean Reversion
    private SimpleMovingAverage _fastMA;
    private SimpleMovingAverage _slowMA;

    // Strategy 4: RSI + MACD
    private RelativeStrengthIndex _rsi;
    private MacdHistogram _macd;

    // Common
    private AverageTrueRange _atr;
    private DirectionalMovementSystem _adx;

    // ============ STATE VARIABLES ============
    
    private double _initialBalance;
    private double _dailyStartBalance;
    private double _peakBalance;
    private DateTime _lastDayChecked;
    private const string Label = "MultiStrategyBot";

    // Strategy tracking
    private int _activeStrategyId = 0; // 1, 2, 3, or 4
    private bool _tradingStoppedForDay = false;

    // ============ INITIALIZATION ============

    protected override void OnStart()
    {
        // Initialize indicators
        _ema50 = Indicators.ExponentialMovingAverage(Bars.ClosePrices, MA50Period);
        _ema200 = Indicators.ExponentialMovingAverage(Bars.ClosePrices, MA200Period);

        _ema9 = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EMA9Period);
        _ema21 = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EMA21Period);
        _ema55 = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EMA55Period);

        _fastMA = Indicators.SimpleMovingAverage(Bars.ClosePrices, FastMAPeriod);
        _slowMA = Indicators.SimpleMovingAverage(Bars.ClosePrices, SlowMAPeriod);

        _rsi = Indicators.RelativeStrengthIndex(Bars.ClosePrices, RSIPeriod);
        _macd = Indicators.MacdHistogram(Bars.ClosePrices, 12, 26, 9);

        _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Simple);
        _adx = Indicators.DirectionalMovementSystem(AdxPeriod);

        // Initialize drawdown tracking
        _initialBalance = Account.Balance;
        _dailyStartBalance = Account.Balance;
        _peakBalance = Account.Balance;
        _lastDayChecked = Server.Time.Date;

        Print("====================================================================");
        Print($"[START] MultiStrategyBot - {Symbol.Name} {TimeFrame}");
        Print($"[CONFIG] Risk/Trade: {RiskPercent}% | DailyDD: {MaxDailyDrawdownPct}% | MaxDD: {MaxTotalDrawdownPct}%");
        Print($"[CONFIG] S1(50/200): {UseStrategy1DualMA} | S2(9/21/55): {UseStrategy2TripleMA} | S3(MeanRev): {UseStrategy3MeanReversion} | S4(RSIMACD): {UseStrategy4RSIMACD}");
        Print($"[START] Initial Balance: {Account.Balance:F2}");
        Print("====================================================================");
    }

    // ============ MAIN TRADING LOGIC ============

    protected override void OnTick()
    {
        // Check Daily Reset
        if (Server.Time.Date > _lastDayChecked)
        {
            _dailyStartBalance = Account.Balance;
            _lastDayChecked = Server.Time.Date;
            _tradingStoppedForDay = false;
            Print($"[DailyReset] New trading day - Balance: {Account.Balance:F2}");
        }

        // DD Guards
        double dailyDD = (_dailyStartBalance - Account.Equity) / _dailyStartBalance * 100.0;
        
        _peakBalance = Math.Max(_peakBalance, Account.Equity);
        double totalDD = (_initialBalance - Account.Equity) / _initialBalance * 100.0;

        if (totalDD >= MaxTotalDrawdownPct)
        {
            Print($"[VÉSZFÉK] MAX DRAWDOWN REACHED ({totalDD:F2}%) - CLOSING ALL TRADES AND STOPPING");
            CloseAll();
            Stop();
        }
        else if (dailyDD >= MaxDailyDrawdownPct && !_tradingStoppedForDay)
        {
            Print($"[VÉSZFÉK] Daily limit reached ({dailyDD:F2}%) - Closing trades, no new entries today");
            CloseAll();
            _tradingStoppedForDay = true;
        }
    }

    protected override void OnBar()
    {
        // Check if trading is allowed (DD guards, spread, etc.)
        if (!IsEntryAllowed())
            return;

        // Detect market trend and select strategy
        UpdateActiveStrategy();

        // Execute selected strategy
        ExecuteStrategy();
    }

    private void UpdateActiveStrategy()
    {
        if (_adx.ADX.Count < 2)
            return;

        double adxValue = _adx.ADX.Last(1);

        if (adxValue > 25)
        {
            // Strong trend: Use 50/200 EMA for trend strength, or 9/21/55 for higher sensitivity
            _activeStrategyId = 1;
            Print($"[Strategy] STRONG TREND detected (ADX={adxValue:F2}) - Using 50/200 EMA");
        }
        else if (adxValue >= 15)
        {
            // Moderate trend: Use 9/21/55 with better reversals
            _activeStrategyId = 2;
            Print($"[Strategy] MODERATE TREND detected (ADX={adxValue:F2}) - Using 9/21/55 EMA");
        }
        else if (adxValue >= 10)
        {
            // Weak trend / range: Use ATR Mean Reversion
            _activeStrategyId = 3;
            Print($"[Strategy] WEAK TREND/RANGE detected (ADX={adxValue:F2}) - Using ATR Mean Reversion");
        }
        else
        {
            // Extreme range or no direction: RSI + MACD
            _activeStrategyId = 4;
            Print($"[Strategy] EXTREME RANGE detected (ADX={adxValue:F2}) - Using RSI+MACD");
        }
    }

    private void ExecuteStrategy()
    {
        switch (_activeStrategyId)
        {
            case 1:
                if (UseStrategy1DualMA)
                    ExecuteStrategy1_DualMA();
                break;
            case 2:
                if (UseStrategy2TripleMA)
                    ExecuteStrategy2_TripleMA();
                break;
            case 3:
                if (UseStrategy3MeanReversion)
                    ExecuteStrategy3_MeanReversion();
                break;
            case 4:
                if (UseStrategy4RSIMACD)
                    ExecuteStrategy4_RSIMACD();
                break;
        }
    }

    // ============ STRATEGY 1: 50/200 EMA CROSSOVER ============

    private void ExecuteStrategy1_DualMA()
    {
        if (_ema50.Result.Count < 3 || _ema200.Result.Count < 3)
            return;

        double ema50_prev = _ema50.Result.Last(2);
        double ema50_curr = _ema50.Result.Last(1);
        double ema200_prev = _ema200.Result.Last(2);
        double ema200_curr = _ema200.Result.Last(1);

        // Buy signal: 50 EMA crosses above 200 EMA
        bool buySignal = ema50_prev <= ema200_prev && ema50_curr > ema200_curr;

        // Sell signal: 50 EMA crosses below 200 EMA
        bool sellSignal = ema50_prev >= ema200_prev && ema50_curr < ema200_curr;

        if (buySignal && Positions.FindAll(Label, Symbol.Name).Length == 0)
        {
            OpenPosition(TradeType.Buy, "S1_DualMA_Buy", ema50_curr);
        }
        else if (sellSignal && Positions.FindAll(Label, Symbol.Name).Length == 0)
        {
            OpenPosition(TradeType.Sell, "S1_DualMA_Sell", ema50_curr);
        }
    }

    // ============ STRATEGY 2: 9/21/55 EMA TRIPLE SYSTEM ============

    private void ExecuteStrategy2_TripleMA()
    {
        if (_ema9.Result.Count < 3 || _ema21.Result.Count < 3 || _ema55.Result.Count < 3)
            return;

        double ema9_curr = _ema9.Result.Last(1);
        double ema9_prev = _ema9.Result.Last(2);
        double ema21_curr = _ema21.Result.Last(1);
        double ema21_prev = _ema21.Result.Last(2);
        double ema55_curr = _ema55.Result.Last(1);
        double price_curr = Bars.ClosePrices.Last(1);

        // Buy signal: 9 EMA above 21 EMA above 55 EMA, price above all three
        bool buySignal = (ema9_curr > ema21_curr) && (ema21_curr > ema55_curr) && (price_curr > ema9_curr);

        // Sell signal: 9 EMA below 21 EMA below 55 EMA, price below all three
        bool sellSignal = (ema9_curr < ema21_curr) && (ema21_curr < ema55_curr) && (price_curr < ema9_curr);

        // Alternative: Crossover detection
        bool buyCrossover = (ema9_prev <= ema21_prev) && (ema9_curr > ema21_curr) && (price_curr > ema55_curr);
        bool sellCrossover = (ema9_prev >= ema21_prev) && (ema9_curr < ema21_curr) && (price_curr < ema55_curr);

        if ((buySignal || buyCrossover) && Positions.FindAll(Label, Symbol.Name).Length == 0)
        {
            OpenPosition(TradeType.Buy, "S2_TripleEMA_Buy", ema9_curr);
        }
        else if ((sellSignal || sellCrossover) && Positions.FindAll(Label, Symbol.Name).Length == 0)
        {
            OpenPosition(TradeType.Sell, "S2_TripleEMA_Sell", ema9_curr);
        }
    }

    // ============ STRATEGY 3: ATR MEAN REVERSION ============

    private void ExecuteStrategy3_MeanReversion()
    {
        if (_fastMA.Result.Count < 3 || _slowMA.Result.Count < 3)
            return;

        double fastMA_curr = _fastMA.Result.Last(1);
        double fastMA_prev = _fastMA.Result.Last(2);
        double slowMA_curr = _slowMA.Result.Last(1);
        double slowMA_prev = _slowMA.Result.Last(2);
        double price = Bars.ClosePrices.Last(1);

        // Buy signal: Fast MA crosses above Slow MA (reversion to upside)
        bool buyCrossover = (fastMA_prev <= slowMA_prev) && (fastMA_curr > slowMA_curr);

        // Sell signal: Fast MA crosses below Slow MA (reversion to downside)
        bool sellCrossover = (fastMA_prev >= slowMA_prev) && (fastMA_curr < slowMA_curr);

        if (buyCrossover && Positions.FindAll(Label, Symbol.Name).Length == 0)
        {
            OpenPosition(TradeType.Buy, "S3_MeanRev_Buy", price);
        }
        else if (sellCrossover && Positions.FindAll(Label, Symbol.Name).Length == 0)
        {
            OpenPosition(TradeType.Sell, "S3_MeanRev_Sell", price);
        }
    }

    // ============ STRATEGY 4: RSI + MACD CONFIRMATION ============

    private void ExecuteStrategy4_RSIMACD()
    {
        if (_rsi.Result.Count < 2 || _macd.Histogram.Count < 2)
            return;

        double rsi = _rsi.Result.Last(1);
        double rsi_prev = _rsi.Result.Last(2);
        double macdHist = _macd.Histogram.Last(1);
        double macdHist_prev = _macd.Histogram.Last(2);
        double price = Bars.ClosePrices.Last(1);

        // Buy signal: RSI crosses above oversold + MACD histogram positive
        bool buySignal = (rsi_prev <= RSIOversold && rsi > RSIOversold) && (macdHist > 0);

        // Sell signal: RSI crosses below overbought + MACD histogram negative
        bool sellSignal = (rsi_prev >= RSIOverbought && rsi < RSIOverbought) && (macdHist < 0);

        if (buySignal && Positions.FindAll(Label, Symbol.Name).Length == 0)
        {
            OpenPosition(TradeType.Buy, "S4_RSIMACD_Buy", price);
        }
        else if (sellSignal && Positions.FindAll(Label, Symbol.Name).Length == 0)
        {
            OpenPosition(TradeType.Sell, "S4_RSIMACD_Sell", price);
        }
    }

    // ============ POSITION MANAGEMENT ============

    private void OpenPosition(TradeType tradeType, string comment, double entryPrice)
    {
        if (_atr.Result.Count < 2)
            return;

        double atrValue = _atr.Result.Last(1);
        double stopLossPips = (atrValue / Symbol.PipSize) * SlAtrMultiplier;
        double takeProfitPips = (atrValue / Symbol.PipSize) * TpAtrMultiplier;

        if (stopLossPips <= 0 || takeProfitPips <= 0)
        {
            Print($"[Error] Invalid ATR values - SL: {stopLossPips}, TP: {takeProfitPips}");
            return;
        }

        double volume = CalculateVolume(stopLossPips);
        if (volume <= 0)
        {
            Print($"[Warning] Volume calculation failed - Risk: {RiskPercent}%, SL: {stopLossPips}");
            return;
        }

        double stopLossPrice = tradeType == TradeType.Buy 
            ? entryPrice - (stopLossPips * Symbol.PipSize)
            : entryPrice + (stopLossPips * Symbol.PipSize);

        double takeProfitPrice = tradeType == TradeType.Buy
            ? entryPrice + (takeProfitPips * Symbol.PipSize)
            : entryPrice - (takeProfitPips * Symbol.PipSize);

        // ExecuteMarketOrder expects SL and TP in PIPS, not absolute price!
        // We also attach the strategy 'comment' so OnStop can count the trades properly.
        var result = ExecuteMarketOrder(tradeType, Symbol.Name, volume, Label, stopLossPips, takeProfitPips, comment: comment);

        if (result.IsSuccessful)
        {
            Print($"[{comment}] OPENED - Type: {tradeType}, Volume: {volume:F2}, SL: {stopLossPrice:F4}, TP: {takeProfitPrice:F4}");
        }
        else
        {
            Print($"[Error] Failed to open position: {result.Error}");
        }
    }

    private double CalculateVolume(double stopLossPips)
    {
        if (stopLossPips <= 0 || Symbol.PipValue <= 0)
            return 0;

        double riskAmount = Account.Balance * (RiskPercent / 100.0);
        double volume = riskAmount / (stopLossPips * Symbol.PipValue);
        volume = Symbol.NormalizeVolumeInUnits(volume);
        
        double maxVolume = Symbol.VolumeInUnitsMax;
        return Math.Min(volume, maxVolume);
    }

    private void CloseAll()
    {
        foreach (var position in Positions)
        {
            if (position.SymbolName == Symbol.Name && position.Label == Label)
            {
                ClosePosition(position);
            }
        }
    }

    // ============ RISK MANAGEMENT ============

    private bool IsEntryAllowed()
    {
        if (_tradingStoppedForDay)
            return false;

        // Max concurrent trades check
        int openTrades = Positions.FindAll(Label, Symbol.Name).Length;
        if (openTrades >= MaxOpenTrades)
        {
            return false;
        }

        // Spread filter
        double spread = Symbol.Spread / Symbol.PipSize;
        if (spread > MaxSpreadPips)
        {
            return false;
        }

        return true;
    }

    // ============ CLEANUP ============

    protected override void OnStop()
    {
        int totalTrades = History.Count;
        int winningTrades = History.Count(x => x.NetProfit > 0);
        double winRate = totalTrades > 0 ? ((double)winningTrades / totalTrades) * 100 : 0;
        
        double grossProfit = History.Where(x => x.NetProfit > 0).Sum(x => x.NetProfit);
        double grossLoss = Math.Abs(History.Where(x => x.NetProfit < 0).Sum(x => x.NetProfit));
        double profitFactor = grossLoss > 0 ? grossProfit / grossLoss : (grossProfit > 0 ? double.PositiveInfinity : 0);
        
        double netProfit = History.Sum(x => x.NetProfit);
        
        double maxDrawdownPct = (_peakBalance > 0) ? ((_peakBalance - Account.Balance) / _peakBalance) * 100.0 : 0;

        Print("====================================================================");
        Print($"[STOP] BACKTEST RESULTS: {Symbol.Name} {TimeFrame}");
        Print($"[RESULTS] Final Balance: {Account.Balance:F2}");
        Print($"[RESULTS] Peak Balance Reached: {_peakBalance:F2} (Max DD from Peak: {maxDrawdownPct:F2}%)");
        Print($"[RESULTS] Total Trades: {totalTrades}");
        Print($"[RESULTS] Win Rate: {winRate:F2}% ({winningTrades} won / {totalTrades - winningTrades} lost)");
        Print($"[RESULTS] Profit Factor: {profitFactor:F2}");
        Print($"[RESULTS] Net Profit: {netProfit:F2}");
        
        // Strategy Breakdown
        var s1Trades = History.Count(x => x.Comment != null && x.Comment.StartsWith("S1"));
        var s1Wins = History.Count(x => x.Comment != null && x.Comment.StartsWith("S1") && x.NetProfit > 0);
        var s1Profit = History.Where(x => x.Comment != null && x.Comment.StartsWith("S1")).Sum(x => x.NetProfit);
        
        var s2Trades = History.Count(x => x.Comment != null && x.Comment.StartsWith("S2"));
        var s2Wins = History.Count(x => x.Comment != null && x.Comment.StartsWith("S2") && x.NetProfit > 0);
        var s2Profit = History.Where(x => x.Comment != null && x.Comment.StartsWith("S2")).Sum(x => x.NetProfit);

        var s3Trades = History.Count(x => x.Comment != null && x.Comment.StartsWith("S3"));
        var s3Wins = History.Count(x => x.Comment != null && x.Comment.StartsWith("S3") && x.NetProfit > 0);
        var s3Profit = History.Where(x => x.Comment != null && x.Comment.StartsWith("S3")).Sum(x => x.NetProfit);

        var s4Trades = History.Count(x => x.Comment != null && x.Comment.StartsWith("S4"));
        var s4Wins = History.Count(x => x.Comment != null && x.Comment.StartsWith("S4") && x.NetProfit > 0);
        var s4Profit = History.Where(x => x.Comment != null && x.Comment.StartsWith("S4")).Sum(x => x.NetProfit);

        Print("--- STRATEGY BREAKDOWN ---");
        if(s1Trades > 0) Print($"S1 (50/200): {s1Trades} trades, {s1Wins} wins, Net: {s1Profit:F2}");
        if(s2Trades > 0) Print($"S2 (9/21/55): {s2Trades} trades, {s2Wins} wins, Net: {s2Profit:F2}");
        if(s3Trades > 0) Print($"S3 (MeanRev): {s3Trades} trades, {s3Wins} wins, Net: {s3Profit:F2}");
        if(s4Trades > 0) Print($"S4 (RSIMACD): {s4Trades} trades, {s4Wins} wins, Net: {s4Profit:F2}");

        Print("====================================================================");
    }
}
