//+------------------------------------------------------------------+
//|  XAUUSD M15 Breakout & Retest Strategy                          |
//|  Timeframe  : M15                                                |
//|  Target     : ~5% monthly profit                                 |
//|  Max SL     : 1% of account balance per trade                    |
//|  Max trades : 1 per day                                          |
//+------------------------------------------------------------------+
#property copyright "XAUUSD Breakout Retest EA"
#property version   "1.00"
#property strict

#include <Trade\Trade.mqh>
#include <Trade\PositionInfo.mqh>

//--- Input parameters
input group "=== Strategy Settings ==="
input int    SwingLookback      = 20;      // Swing high/low lookback (candles)
input int    MaxRetestCandles   = 6;       // Max candles after breakout to wait for retest
input double RetestZonePct      = 0.15;    // Retest zone tolerance (% of breakout range)
input double MinBreakoutPips    = 15.0;    // Minimum breakout size (pips / points for XAUUSD)
input double RiskRewardRatio    = 2.5;     // Take Profit = SL * RR
input bool   UseHourFilter      = true;    // Only trade during active sessions
input int    SessionStartHour   = 7;       // Session start hour (UTC)
input int    SessionEndHour     = 18;      // Session end hour (UTC)

input group "=== Risk Management ==="
input double MaxRiskPct         = 1.0;     // Max risk per trade (% of balance)
input double MinRiskPct         = 0.5;     // Min risk per trade (% of balance)

input group "=== ATR Filter ==="
input bool   UseATRFilter       = true;    // Enable ATR volatility filter
input int    ATRPeriod          = 14;      // ATR period
input double ATRMultiplierMin   = 0.5;     // Min ATR multiplier (avoid low vol)
input double ATRMultiplierMax   = 3.0;     // Max ATR multiplier (avoid spikes)

input group "=== H1 Trend Filter ==="
input bool   UseTrendFilter     = true;    // Only trade in H1 EMA trend direction
input int    TrendEMAPeriod     = 50;      // H1 EMA period for trend filter

//--- Global variables
CTrade   trade;
int      atrHandle_m15;
int      emaHandle_h1;

datetime lastTradeDay    = 0;
int      breakoutDir     = 0;      // 1 = bullish breakout, -1 = bearish breakout
double   breakoutLevel   = 0.0;    // The broken S/R level
int      retestCount     = 0;      // Candles since breakout
bool     waitingRetest   = false;
double   atrAtBreakout   = 0.0;

//+------------------------------------------------------------------+
//| Expert initialization                                            |
//+------------------------------------------------------------------+
int OnInit()
  {
   if(_Symbol != "XAUUSD" && _Symbol != "XAUUSDm" && _Symbol != "GOLD")
      Print("WARNING: This EA is optimised for XAUUSD. Current symbol: ", _Symbol);

   if(_Period != PERIOD_M15)
     {
      Alert("EA must run on M15 chart!");
      return INIT_FAILED;
     }

   atrHandle_m15 = iATR(_Symbol, PERIOD_M15, ATRPeriod);
   emaHandle_h1  = iMA(_Symbol, PERIOD_H1, TrendEMAPeriod, 0, MODE_EMA, PRICE_CLOSE);

   if(atrHandle_m15 == INVALID_HANDLE || emaHandle_h1 == INVALID_HANDLE)
     {
      Alert("Failed to create indicator handles!");
      return INIT_FAILED;
     }

   trade.SetExpertMagicNumber(202601);
   trade.SetDeviationInPoints(20);
   trade.SetTypeFilling(ORDER_FILLING_IOC);

   Print("XAUUSD Breakout & Retest EA initialized.");
   return INIT_SUCCEEDED;
  }

//+------------------------------------------------------------------+
//| Expert deinitialization                                          |
//+------------------------------------------------------------------+
void OnDeinit(const int reason)
  {
   IndicatorRelease(atrHandle_m15);
   IndicatorRelease(emaHandle_h1);
  }

//+------------------------------------------------------------------+
//| Expert tick function                                             |
//+------------------------------------------------------------------+
void OnTick()
  {
   //--- Only process on new M15 candle close
   static datetime lastBarTime = 0;
   datetime currentBarTime = iTime(_Symbol, PERIOD_M15, 0);
   if(currentBarTime == lastBarTime)
      return;
   lastBarTime = currentBarTime;

   //--- Already have an open position - skip
   if(PositionsTotal() > 0)
      return;

   //--- Already traded today
   MqlDateTime dt;
   TimeToStruct(TimeCurrent(), dt);
   MqlDateTime lastDt;
   TimeToStruct(lastTradeDay, lastDt);
   if(lastTradeDay > 0 && dt.day == lastDt.day && dt.mon == lastDt.mon && dt.year == lastDt.year)
      return;

   //--- Session hour filter
   if(UseHourFilter && (dt.hour < SessionStartHour || dt.hour >= SessionEndHour))
      return;

   //--- Get ATR value
   double atrBuf[];
   ArraySetAsSeries(atrBuf, true);
   if(CopyBuffer(atrHandle_m15, 0, 1, 3, atrBuf) < 3)
      return;
   double currentATR = atrBuf[0];

   //--- ATR filter
   if(UseATRFilter)
     {
      double avgATR = (atrBuf[0] + atrBuf[1] + atrBuf[2]) / 3.0;
      if(currentATR < avgATR * ATRMultiplierMin || currentATR > avgATR * ATRMultiplierMax)
         return;
     }

   //--- Get H1 trend direction
   int trendDir = 0;
   if(UseTrendFilter)
     {
      double emaBuf[];
      ArraySetAsSeries(emaBuf, true);
      if(CopyBuffer(emaHandle_h1, 0, 0, 2, emaBuf) < 2)
         return;
      double closeH1 = iClose(_Symbol, PERIOD_H1, 0);
      trendDir = (closeH1 > emaBuf[0]) ? 1 : -1;
     }

   //--- Find swing high and low
   double swingHigh = FindSwingHigh(SwingLookback);
   double swingLow  = FindSwingLow(SwingLookback);
   if(swingHigh <= 0 || swingLow <= 0)
      return;

   //--- Current candle data (last closed candle = index 1)
   double prevClose = iClose(_Symbol, PERIOD_M15, 1);
   double prevOpen  = iOpen(_Symbol, PERIOD_M15, 1);
   double prevHigh  = iHigh(_Symbol, PERIOD_M15, 1);
   double prevLow   = iLow(_Symbol, PERIOD_M15, 1);
   double curClose  = iClose(_Symbol, PERIOD_M15, 0);

   //--- Check for new breakout if not already waiting for retest
   if(!waitingRetest)
     {
      double minBreakout = MinBreakoutPips * _Point * 10; // Convert pips to price for XAUUSD

      //--- Bullish breakout: previous candle closed above swing high
      if(prevClose > swingHigh + minBreakout && (!UseTrendFilter || trendDir == 1))
        {
         breakoutDir   = 1;
         breakoutLevel = swingHigh;
         retestCount   = 0;
         waitingRetest = true;
         atrAtBreakout = currentATR;
         Print("Bullish breakout detected at ", breakoutLevel);
        }
      //--- Bearish breakout: previous candle closed below swing low
      else if(prevClose < swingLow - minBreakout && (!UseTrendFilter || trendDir == -1))
        {
         breakoutDir   = -1;
         breakoutLevel = swingLow;
         retestCount   = 0;
         waitingRetest = true;
         atrAtBreakout = currentATR;
         Print("Bearish breakout detected at ", breakoutLevel);
        }
     }
   else
     {
      //--- We are waiting for a retest
      retestCount++;

      //--- Timeout: too many candles without retest
      if(retestCount > MaxRetestCandles)
        {
         waitingRetest = false;
         breakoutDir   = 0;
         Print("Retest timeout - resetting breakout state");
         return;
        }

      double zoneSize    = atrAtBreakout * RetestZonePct;
      double zoneUpper   = breakoutLevel + zoneSize;
      double zoneLower   = breakoutLevel - zoneSize;

      if(breakoutDir == 1)
        {
         //--- Bullish retest: price returns to breakout level (former resistance = new support)
         if(prevLow <= zoneUpper && prevHigh >= zoneLower)
           {
            //--- Confirmation: bullish engulfing or close above open (bullish candle)
            bool bullishCandle = (prevClose > prevOpen) &&
                                 ((prevClose - prevOpen) > 0.3 * (prevHigh - prevLow));
            if(bullishCandle)
              {
               EnterTrade(ORDER_TYPE_BUY, prevLow, currentATR);
              }
           }
        }
      else if(breakoutDir == -1)
        {
         //--- Bearish retest: price returns to breakout level (former support = new resistance)
         if(prevHigh >= zoneLower && prevLow <= zoneUpper)
           {
            //--- Confirmation: bearish candle - close below open
            bool bearishCandle = (prevClose < prevOpen) &&
                                 ((prevOpen - prevClose) > 0.3 * (prevHigh - prevLow));
            if(bearishCandle)
              {
               EnterTrade(ORDER_TYPE_SELL, prevHigh, currentATR);
              }
           }
        }
     }
  }

//+------------------------------------------------------------------+
//| Enter a trade with proper risk management                        |
//+------------------------------------------------------------------+
void EnterTrade(ENUM_ORDER_TYPE orderType, double retestExtreme, double atr)
  {
   double ask = SymbolInfoDouble(_Symbol, SYMBOL_ASK);
   double bid = SymbolInfoDouble(_Symbol, SYMBOL_BID);
   double balance = AccountInfoDouble(ACCOUNT_BALANCE);

   double slDist, tpDist, entryPrice, sl, tp;

   if(orderType == ORDER_TYPE_BUY)
     {
      entryPrice = ask;
      //--- SL below retest candle low with small buffer
      slDist     = MathMax(ask - retestExtreme + atr * 0.25, atr * 0.5);
      sl         = entryPrice - slDist;
      tpDist     = slDist * RiskRewardRatio;
      tp         = entryPrice + tpDist;
     }
   else
     {
      entryPrice = bid;
      slDist     = MathMax(retestExtreme - bid + atr * 0.25, atr * 0.5);
      sl         = entryPrice + slDist;
      tpDist     = slDist * RiskRewardRatio;
      tp         = entryPrice - tpDist;
     }

   //--- Cap SL at MaxRiskPct of balance
   double maxRiskMoney  = balance * MaxRiskPct / 100.0;
   double minRiskMoney  = balance * MinRiskPct / 100.0;

   //--- Calculate lot size based on risk
   double tickValue = SymbolInfoDouble(_Symbol, SYMBOL_TRADE_TICK_VALUE);
   double tickSize  = SymbolInfoDouble(_Symbol, SYMBOL_TRADE_TICK_SIZE);
   double lotStep   = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_STEP);
   double minLot    = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MIN);
   double maxLot    = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MAX);

   if(tickValue <= 0 || tickSize <= 0)
     {
      Print("Invalid tick value/size - skipping trade");
      return;
     }

   double slPoints  = slDist / tickSize;
   double lotSize   = maxRiskMoney / (slPoints * tickValue);
   lotSize = MathFloor(lotSize / lotStep) * lotStep;
   lotSize = MathMax(minLot, MathMin(maxLot, lotSize));

   //--- Sanity check: actual risk with this lot
   double actualRisk = lotSize * slPoints * tickValue;
   if(actualRisk > balance * MaxRiskPct / 100.0 * 1.1)  // 10% tolerance
     {
      Print("Risk check failed: actual risk = ", actualRisk, " > max allowed");
      return;
     }

   //--- Normalize prices
   int digits = (int)SymbolInfoInteger(_Symbol, SYMBOL_DIGITS);
   sl = NormalizeDouble(sl, digits);
   tp = NormalizeDouble(tp, digits);

   //--- Place the order
   bool result = false;
   if(orderType == ORDER_TYPE_BUY)
      result = trade.Buy(lotSize, _Symbol, ask, sl, tp, "XAUUSD BRK+RT Long");
   else
      result = trade.Sell(lotSize, _Symbol, bid, sl, tp, "XAUUSD BRK+RT Short");

   if(result)
     {
      lastTradeDay  = TimeCurrent();
      waitingRetest = false;
      breakoutDir   = 0;
      double rr = tpDist / slDist;
      PrintFormat("Trade opened: %s | Lot=%.2f | Entry=%.3f | SL=%.3f | TP=%.3f | RR=1:%.1f | Risk=$%.2f",
                  orderType == ORDER_TYPE_BUY ? "BUY" : "SELL",
                  lotSize, entryPrice, sl, tp, rr, actualRisk);
     }
   else
     {
      Print("Order failed: ", trade.ResultRetcodeDescription());
     }
  }

//+------------------------------------------------------------------+
//| Find the swing high over last N candles (excluding index 0)      |
//+------------------------------------------------------------------+
double FindSwingHigh(int lookback)
  {
   double highest = 0;
   for(int i = 2; i <= lookback + 2; i++)
     {
      double h = iHigh(_Symbol, PERIOD_M15, i);
      if(h > highest)
         highest = h;
     }
   return highest;
  }

//+------------------------------------------------------------------+
//| Find the swing low over last N candles (excluding index 0)       |
//+------------------------------------------------------------------+
double FindSwingLow(int lookback)
  {
   double lowest = DBL_MAX;
   for(int i = 2; i <= lookback + 2; i++)
     {
      double l = iLow(_Symbol, PERIOD_M15, i);
      if(l < lowest)
         lowest = l;
     }
   return (lowest == DBL_MAX) ? 0 : lowest;
  }

//+------------------------------------------------------------------+
//| Trade event handler                                              |
//+------------------------------------------------------------------+
void OnTradeTransaction(const MqlTradeTransaction &trans,
                        const MqlTradeRequest &request,
                        const MqlTradeResult &result)
  {
   if(trans.type == TRADE_TRANSACTION_DEAL_ADD)
     {
      if(trans.deal_type == DEAL_TYPE_BUY || trans.deal_type == DEAL_TYPE_SELL)
         Print("Deal executed: ticket=", trans.deal, " profit=", trans.price);
     }
  }
//+------------------------------------------------------------------+
