from robot_wrapper import *
import pandas as pd
from datetime import time
import clr
clr.AddReference("cAlgo.API")
from cAlgo.API import TradeType

class DanielH_breakout:
    def on_start(self):
        # --- FTMO 10K KONFIGURÁCIÓ ---
        self.initial_deposit = 10000.0
        self.max_loss_pct = 0.10       # 10% Összesített
        self.max_daily_loss_pct = 0.05   # 5% Napi
        
        # Kezdő állapotok
        self.daily_start_equity = api.Account.Equity
        self.absolute_floor = self.initial_deposit * (1 - self.max_loss_pct)
        
        # --- ÁLLAPOT GÉP (Spam védelem) ---
        self.be_moved = set()
        self.partial_closed = set()
        
        # --- STRATÉGIA PARAMÉTEREK (XAUUSD M15) ---
        self.lookback_period = 25
        self.swing_depth = 8            # Gyorsabb trendfelismeréshez
        self.max_dist_pips = 600.0      # Levittük 600-ra (max $6.00 elmozdulás a szinttől)
        self.min_body_ratio = 0.45      
        
        # --- RISK & MANAGEMENT ---
        self.risk_per_trade_pct = 1.0  
        self.stop_loss_pips = 400.0     # Ez most a BÁZIS (minimum) SL távolság a szinttől!
        self.partial_tp_pips = 600.0    # ~ $6.0-nál részleges zárás
        self.partial_volume_pct = 0.7
        self.break_even_pips = 400.0    # ~ $4.0-nál StopLoss húzása nullába
        
        # --- IDŐZÍTÉS & FILTEREK ---
        self.start_time_val = 900
        self.end_time_val = 1500
        self.long_only = True           # Csak vételi pozíciók
        
        api.Timer.Start(3600)
        print(f"--- DANIEL HOLMES FTMO XAUUSD BOT START ---")
        print(f"Abszolút padló: {self.absolute_floor} USD")
        print(f"Stratégia: MARKET Order Belépés Dinamikus SL/TP-vel, Spam Szűrő ON.")

    def get_dynamic_volume(self, custom_sl_pips):
        """Kiszámolja a normalizált egységszámot a dinamikus SL alapján."""
        risk_amount = api.Account.Balance * (self.risk_per_trade_pct / 100.0)
        
        current_daily_limit = self.daily_start_equity * self.max_daily_loss_pct
        if risk_amount > (current_daily_limit * 0.5):
            risk_amount = current_daily_limit * 0.5

        pip_value = api.Symbol.PipValue
        if pip_value == 0: 
            return 0
        
        raw_volume = risk_amount / (custom_sl_pips * pip_value)
        
        # Matematikai normalizálás
        step = api.Symbol.VolumeInUnitsStep
        min_vol = api.Symbol.VolumeInUnitsMin
        
        normalized_volume = (raw_volume // step) * step
        
        if normalized_volume < min_vol:
            return 0
            
        return normalized_volume

    def check_ftmo_safety(self):
        curr_equity = api.Account.Equity
        daily_loss = self.daily_start_equity - curr_equity
        daily_limit_amt = self.daily_start_equity * self.max_daily_loss_pct
        
        if curr_equity <= self.absolute_floor:
            self.stop_logic("ABSZOLÚT MAX LOSS ELÉRVE!")
            return False

        if daily_loss >= daily_limit_amt:
            self.stop_logic(f"NAPI LIMIT ELÉRVE! (Veszteség: {daily_loss:.2f})")
            return False
            
        return True

    def stop_logic(self, reason):
        print(f"!!! STOP: {reason} !!!")
        for pos in api.Positions:
            close_position(pos)
        for order in api.PendingOrders:
            api.CancelPendingOrder(order)
        api.Stop()

    def on_bar(self):
        # Éjféli reset szerveridő szerint
        if api.Server.Time.Hour == 0 and api.Server.Time.Minute == 0:
            self.daily_start_equity = api.Account.Equity
            self.be_moved.clear()
            self.partial_closed.clear()
            print(f"[RESET] Új nap. Kezdőtőke: {self.daily_start_equity:.2f}, State halmazok törölve.")

        # Időablak ellenőrzés
        current_time = api.Server.Time.Hour * 100 + api.Server.Time.Minute
        if not (self.start_time_val <= current_time <= self.end_time_val):
            return

        if api.Bars.Count < 100:
            return

        # 1. KOCKÁZATVÉDELEM: Egyszerre csak 1 nyitott/függő pozíció lehet a piacon
        has_trades = False
        for p in api.Positions:
            if p.Label == "Holmes_FTMO": has_trades = True
        for o in api.PendingOrders:
            if o.Label == "Holmes_FTMO": has_trades = True
            
        if has_trades:
            return

        # INDEXELÉS a cTrader DataSeries API-val
        start_lookback = self.lookback_period + 1
        
        res = max([api.Bars.HighPrices.Last(i) for i in range(2, start_lookback + 1)])
        sup = min([api.Bars.LowPrices.Last(i) for i in range(2, start_lookback + 1)])
        
        curr_close = api.Bars.ClosePrices.Last(1)
        prev_close = api.Bars.ClosePrices.Last(2)
        curr_open = api.Bars.OpenPrices.Last(1)
        curr_high = api.Bars.HighPrices.Last(1)
        curr_low = api.Bars.LowPrices.Last(1)

        dist_long = (curr_close - res) / api.Symbol.PipSize
        dist_short = (sup - curr_close) / api.Symbol.PipSize
        
        full_range = curr_high - curr_low
        body_size = abs(curr_open - curr_close)
        body_ratio = body_size / full_range if full_range > 0 else 0

        # Dinamikus indikátor számítás
        closes = [api.Bars.ClosePrices.Last(i) for i in range(100, 0, -1)]
        df = pd.DataFrame(closes, columns=['Close'])
        
        ema_50 = df['Close'].ewm(span=50, adjust=False).mean().iloc[-1]
        
        delta = df['Close'].diff()
        gain = (delta.where(delta > 0, 0)).rolling(window=14).mean()
        loss = (-delta.where(delta < 0, 0)).rolling(window=14).mean()
        rs = gain / loss
        rsi_14 = 100 - (100 / (1 + rs)).iloc[-1]

        # LONG BELÉPŐ LOGIKA
        if curr_close > res and prev_close <= res:
            print(f"[LOG-M15] --- LONG BREAKOUT ÉSZLELVE ---")
            print(f"[LOG-M15] Árfolyam: {curr_close:.2f} | Ellenállás: {res:.2f}")
            print(f"[LOG-M15] Távolság: {dist_long:.1f} pip (max {self.max_dist_pips}) | Body Ratio: {body_ratio:.2f} (min {self.min_body_ratio})")
            
            if dist_long <= self.max_dist_pips and body_ratio >= self.min_body_ratio:
                print(f"[LOG-M15] EMA50: {ema_50:.2f} | RSI: {rsi_14:.1f} (Elvárás: Ár > EMA50, RSI > 50)")
                
                if curr_close > ema_50 and rsi_14 > 50:
                    lows = [api.Bars.LowPrices.Last(i) for i in range(1, self.swing_depth + 2)]
                    min_low = min(lows)
                    
                    swing_dist = 1
                    for i in range(1, self.swing_depth + 2):
                        if api.Bars.LowPrices.Last(i) == min_low:
                            swing_dist = i
                            break
                    
                    bull_cnt = sum(1 for i in range(1, swing_dist) if api.Bars.ClosePrices.Last(i) > api.Bars.OpenPrices.Last(i))
                    bull_ratio = bull_cnt / (swing_dist - 1) if swing_dist > 1 else 0
                    print(f"[LOG-M15] Trend mélység (swing): {swing_dist} gyertya. Bull Ratio: {bull_ratio:.2f} (min 0.55)")
                    
                    if bull_ratio >= 0.55:
                        # Dinamikus SL kiszámítása (védelem a kitörési szint alatt 400 pippel)
                        dist_to_res_pips = (api.Symbol.Ask - res) / api.Symbol.PipSize
                        dynamic_sl_pips = dist_to_res_pips + self.stop_loss_pips
                        dynamic_tp_pips = dynamic_sl_pips * 3  # 1:3 RR megtartása
                        
                        vol = self.get_dynamic_volume(dynamic_sl_pips)
                        print(f"[LOG-M15] Dinamikus SL: {dynamic_sl_pips:.1f} pip | Dinamikus TP: {dynamic_tp_pips:.1f} pip")
                        print(f"[LOG-M15] Számított volumen (1% kockázattal): {vol}")
                        if vol > 0:
                            print(f"[LOG-M15] >>> MARKET BUY ORDER KÜLDÉSE! <<<")
                            api.ExecuteMarketOrder(TradeType.Buy, api.Symbol.Name, vol, "Holmes_FTMO", dynamic_sl_pips, dynamic_tp_pips)
                        else:
                            print("[LOG-M15] HIBA: A számított volumen 0 lett.")
                    else:
                        print("[LOG-M15] HIBA: Bull Ratio < 0.55")
                else:
                    print("[LOG-M15] HIBA: EMA vagy RSI szűrő blokkolt.")
            else:
                print("[LOG-M15] HIBA: Túl gyenge vagy túl távoli gyertya.")
            print("[LOG-M15] ----------------------------------")

        # SHORT BELÉPŐ LOGIKA
        elif not self.long_only and curr_close < sup and prev_close >= sup:
            print(f"[LOG-M15] --- SHORT BREAKOUT ÉSZLELVE ---")
            if dist_short <= self.max_dist_pips and body_ratio >= self.min_body_ratio:
                if curr_close < ema_50 and rsi_14 < 50:
                    highs = [api.Bars.HighPrices.Last(i) for i in range(1, self.swing_depth + 2)]
                    max_high = max(highs)
                    
                    swing_dist = 1
                    for i in range(1, self.swing_depth + 2):
                        if api.Bars.HighPrices.Last(i) == max_high:
                            swing_dist = i
                            break
                    
                    bear_cnt = sum(1 for i in range(1, swing_dist) if api.Bars.ClosePrices.Last(i) < api.Bars.OpenPrices.Last(i))
                    bear_ratio = bear_cnt / (swing_dist - 1) if swing_dist > 1 else 0
                    
                    if bear_ratio >= 0.55:
                        dist_to_sup_pips = (sup - api.Symbol.Bid) / api.Symbol.PipSize
                        dynamic_sl_pips = dist_to_sup_pips + self.stop_loss_pips
                        dynamic_tp_pips = dynamic_sl_pips * 3
                        
                        vol = self.get_dynamic_volume(dynamic_sl_pips)
                        if vol > 0:
                            print(f"[LOG-M15] >>> MARKET SELL ORDER KÜLDÉSE! <<<")
                            api.ExecuteMarketOrder(TradeType.Sell, api.Symbol.Name, vol, "Holmes_FTMO", dynamic_sl_pips, dynamic_tp_pips)

    def on_tick(self):
        if not self.check_ftmo_safety():
            return

        for pos in api.Positions:
            if pos.Label == "Holmes_FTMO":
                # Partials (State védelemmel)
                if pos.Id not in self.partial_closed and pos.Pips >= self.partial_tp_pips:
                    current_vol = pos.VolumeInUnits
                    step = api.Symbol.VolumeInUnitsStep
                    
                    close_vol = current_vol * self.partial_volume_pct
                    close_vol = (close_vol // step) * step
                    
                    if close_vol >= api.Symbol.VolumeInUnitsMin:
                        print(f"[LOG-TICK] Részleges zárás sikeres (PID: {pos.Id}, Zárt vol: {close_vol})")
                        close_position(pos, close_vol)
                        self.partial_closed.add(pos.Id)
                
                # Break-even (State védelemmel)
                if pos.Id not in self.be_moved and pos.Pips >= self.break_even_pips:
                    print(f"[LOG-TICK] Védő Stop-Loss húzása Break-Even be (PID: {pos.Id})")
                    modify_position(pos, stop_loss=pos.EntryPrice, take_profit=pos.TakeProfit)
                    self.be_moved.add(pos.Id)

    def on_timer(self):
        daily_dd = self.daily_start_equity - api.Account.Equity
        print(f"[STATUS] {api.Server.Time.ToString('HH:mm')} | Equity: {api.Account.Equity:.2f} | Napi DD: {daily_dd:.2f}")

    def on_exception(self, exception):
        print(f"!!! KRITIKUS HIBA ELKAPVA !!!\nÜzenet: {exception}")