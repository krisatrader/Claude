import clr
import math
import System
from System import DayOfWeek
clr.AddReference("cAlgo.API")
from cAlgo.API import *

class LondonGoldStrategy():
    def on_start(self):
        try:
            # -- Stratégiai paraméterek C#-ból (UI Paraméterek) --
            self.ema_period = int(api.EmaPeriod)
            self.rsi_period = int(api.RsiPeriod)
            self.atr_period = int(api.AtrPeriod)
            
            # Kockázatkezelés C#-ból
            self.risk_percent = float(api.RiskPercent)
            self.atr_multiplier = float(api.AtrMultiplier)
            self.reward_ratio = float(api.RewardRatio)
            
            # Időablak C#-ból (UTC szerint megadva)
            self.london_open_utc = int(api.LondonOpenUtc)
            self.london_close_utc = int(api.LondonCloseUtc) 
            
            self.label = "LondonGoldIntraday"
            
            # -- VWAP belső állapot változók --
            self.current_day = -1
            self.cumulative_pv = 0.0
            self.cumulative_vol = 0.0
            
            # -- Statisztika --
            self.stats = {
                "total_bars": 0,
                "out_of_session": 0,
                "open_position": 0,
                "risk_error": 0,
                "no_setup": 0,
                "long_signals": 0,
                "short_signals": 0
            }
            print("London Gold Strategy inicializálva.")
        except Exception as e:
            print(f"Hiba az inicializáláskor: {e}")
        except System.Exception as se:
            print(f"C# Hiba az inicializáláskor: {se}")

    def on_bar_closed(self):
        try:
            if api.Bars.Count < max(self.ema_period, self.rsi_period, self.atr_period) + 2:
                return
                
            closed_bar_time = api.Bars.OpenTimes.Last(1)
            
            high_price = api.Bars.HighPrices.Last(1)
            low_price = api.Bars.LowPrices.Last(1)
            close_price = api.Bars.ClosePrices.Last(1)
            volume = api.Bars.TickVolumes.Last(1)
            
            # --- ADATMINŐSÉG PAJZS (Holiday / Ünnepnap / Hibás adat védelem) ---
            # Ha az árfolyam hiányzik, vagy 0 a forgalom, vagy egyáltalán nincs mozgás (High == Low), 
            # akkor ez egy "halott" vagy fals gyertya, amit a bróker ünnepnapokon generál.
            if high_price is None or math.isnan(high_price) or volume <= 0 or high_price == low_price:
                return
                
            # 1. Napi VWAP (Volume Weighted Average Price) kiszámítása
            if self.current_day != closed_bar_time.Day:
                self.current_day = closed_bar_time.Day
                self.cumulative_pv = 0.0
                self.cumulative_vol = 0.0
                
                msg = f"[{closed_bar_time}] Új nap indult: {closed_bar_time.Date} (Itt vagyok)"
                print(msg)
                
            typical_price = (high_price + low_price + close_price) / 3.0
            
            self.cumulative_pv += (typical_price * volume)
            self.cumulative_vol += volume
            
            vwap = self.cumulative_pv / self.cumulative_vol if self.cumulative_vol > 0 else typical_price
            
            self.stats["total_bars"] += 1
            
            # 2. Időablak szűrés és Pénteki Zárás (Weekend Close)
            current_hour = api.Server.Time.Hour
            
            # Pénteken este zárjuk az összes nyitott pozíciót, hogy ne lépjünk át hétvégére (FTMO szabály)
            # és megelőzzük a cTrader szerver-lezáráskori (22:56:59) erőszakos pozíció-likvidálási hibáját!
            if api.Server.Time.DayOfWeek == DayOfWeek.Friday and current_hour >= 21:
                pos_to_close = None
                for p in api.Positions:
                    if p.Label == self.label:
                        pos_to_close = p
                        break
                if pos_to_close is not None:
                    api.ClosePosition(pos_to_close)
                return
                
            if current_hour < self.london_open_utc or current_hour >= self.london_close_utc:
                self.stats["out_of_session"] += 1
                return 
                
            # 3. Nyitott pozíciók ellenőrzése
            has_position = False
            for p in api.Positions:
                if p.Label == self.label:
                    has_position = True
                    break
                    
            if has_position:
                self.stats["open_position"] += 1
                return
                
            # 4. Indikátor értékek lekérése tiszta Python matematikával (Cloud Kompatibilis)
            lookback = 100
            closes = [api.Bars.ClosePrices.Last(i) for i in range(lookback, -1, -1)]
            highs = [api.Bars.HighPrices.Last(i) for i in range(lookback, -1, -1)]
            lows = [api.Bars.LowPrices.Last(i) for i in range(lookback, -1, -1)]
            
            close_price = closes[-1]
            
            # EMA 21 kiszámítása (Standard EMA)
            k_ema = 2.0 / (self.ema_period + 1)
            ema_val = closes[0]
            for c in closes[1:]:
                ema_val = (c * k_ema) + (ema_val * (1.0 - k_ema))
                
            # RSI 14 kiszámítása (Wilder's Smoothing)
            gains = []
            losses = []
            for i in range(1, len(closes)):
                diff = closes[i] - closes[i-1]
                gains.append(max(0.0, diff))
                losses.append(max(0.0, -diff))
                
            avg_gain = sum(gains[:self.rsi_period]) / self.rsi_period
            avg_loss = sum(losses[:self.rsi_period]) / self.rsi_period
            
            rs_vals = []
            if avg_loss == 0:
                rs_vals.append(100.0)
            else:
                rs_vals.append(100.0 - (100.0 / (1.0 + (avg_gain / avg_loss))))
                
            for i in range(self.rsi_period, len(gains)):
                avg_gain = (avg_gain * (self.rsi_period - 1) + gains[i]) / self.rsi_period
                avg_loss = (avg_loss * (self.rsi_period - 1) + losses[i]) / self.rsi_period
                if avg_loss == 0:
                    rs_vals.append(100.0)
                else:
                    rs_vals.append(100.0 - (100.0 / (1.0 + (avg_gain / avg_loss))))
                    
            rsi_val = rs_vals[-1]
            rsi_prev = rs_vals[-2] if len(rs_vals) > 1 else 50.0
            
            # ATR 14 (Exponential) kiszámítása
            trs = [highs[0] - lows[0]]
            for i in range(1, len(highs)):
                tr1 = highs[i] - lows[i]
                tr2 = abs(highs[i] - closes[i-1])
                tr3 = abs(lows[i] - closes[i-1])
                trs.append(max(tr1, tr2, tr3))
                
            k_atr = 2.0 / (self.atr_period + 1)
            atr_val = trs[0]
            for tr in trs[1:]:
                atr_val = (tr * k_atr) + (atr_val * (1.0 - k_atr))
            
            # NaN ellenőrzés a kezdeti gyertyáknál
            if atr_val is None or math.isnan(atr_val) or atr_val <= 0:
                return
                
            # 5. Kockázatkezelés és Pozícióméretezés
            stop_loss_pips = (atr_val * self.atr_multiplier) / api.Symbol.PipSize
            take_profit_pips = stop_loss_pips * self.reward_ratio

            risk_amount = api.Account.Balance * (self.risk_percent / 100.0)
            pip_value = api.Symbol.PipValue
            
            if stop_loss_pips <= 0 or pip_value <= 0:
                self.stats["risk_error"] += 1
                return
                
            raw_volume = risk_amount / (stop_loss_pips * pip_value)
            volume_step = api.Symbol.VolumeInUnitsStep
            volume_min = api.Symbol.VolumeInUnitsMin
            volume_max = api.Symbol.VolumeInUnitsMax
            
            volume_in_units = int(raw_volume // volume_step) * volume_step
            if volume_in_units < volume_min:
                volume_in_units = volume_min
            elif volume_in_units > volume_max:
                volume_in_units = volume_max

            # 6. Belépési Logika
            is_long_trend = close_price > vwap and close_price > ema_val
            is_long_mom = rsi_val > 50 and rsi_prev <= 50
            
            is_short_trend = close_price < vwap and close_price < ema_val
            is_short_mom = rsi_val < 50 and rsi_prev >= 50
            
            if is_long_trend and is_long_mom:
                self.stats["long_signals"] += 1
                msg = (f"[{closed_bar_time}] 🟢 LONG KÖTÉS INDÍTÁSA\n"
                       f"   - Árfolyam: {close_price:.2f}\n"
                       f"   - Számla: Balance: {api.Account.Balance:.2f} | Kockázat: {risk_amount:.2f} (1%)\n"
                       f"   - Méret: {volume_in_units} units\n"
                       f"   - Paraméterek: SL: {stop_loss_pips:.1f} pip | TP: {take_profit_pips:.1f} pip\n"
                       f"   - Trend Szűrők: Ár > VWAP ({close_price:.2f} > {vwap:.2f}) ÉS Ár > EMA21 ({close_price:.2f} > {ema_val:.2f})\n"
                       f"   - Momentum: RSI kitörés 50 fölé (Aktuális: {rsi_val:.2f}, Előző: {rsi_prev:.2f})\n"
                       f"   - Volatilitás: ATR: {atr_val:.4f}\n"
                       f"   ------------------------------------------------")
                print(msg)
                api.ExecuteMarketOrder(TradeType.Buy, api.SymbolName, volume_in_units, self.label, stop_loss_pips, take_profit_pips)
                
            elif is_short_trend and is_short_mom:
                self.stats["short_signals"] += 1
                msg = (f"[{closed_bar_time}] 🔴 SHORT KÖTÉS INDÍTÁSA\n"
                       f"   - Árfolyam: {close_price:.2f}\n"
                       f"   - Számla: Balance: {api.Account.Balance:.2f} | Kockázat: {risk_amount:.2f} (1%)\n"
                       f"   - Méret: {volume_in_units} units\n"
                       f"   - Paraméterek: SL: {stop_loss_pips:.1f} pip | TP: {take_profit_pips:.1f} pip\n"
                       f"   - Trend Szűrők: Ár < VWAP ({close_price:.2f} < {vwap:.2f}) ÉS Ár < EMA21 ({close_price:.2f} < {ema_val:.2f})\n"
                       f"   - Momentum: RSI letörés 50 alá (Aktuális: {rsi_val:.2f}, Előző: {rsi_prev:.2f})\n"
                       f"   - Volatilitás: ATR: {atr_val:.4f}\n"
                       f"   ------------------------------------------------")
                print(msg)
                api.ExecuteMarketOrder(TradeType.Sell, api.SymbolName, volume_in_units, self.label, stop_loss_pips, take_profit_pips)
            else:
                self.stats["no_setup"] += 1
                
        except Exception as e:
            print(f"Hiba az on_bar_closed-ban: {e}")
        except System.Exception as se:
            print(f"C# Hiba az on_bar_closed-ban: {se}")

    def on_exception(self, exception):
        print(f"Kivétel történt: {exception}")

    def on_stop(self):
        try:
            print("========== BACKTEST BELSŐ STATISZTIKA ==========")
            print(f"Feldolgozott gyertyák: {self.stats['total_bars']}")
            print(f"Időablakon kívül (nincs kötés): {self.stats['out_of_session']}")
            print(f"Már volt nyitott pozíció (nincs kötés): {self.stats['open_position']}")
            print(f"Kockázati paraméter hiba: {self.stats['risk_error']}")
            print(f"Nincs megfelelő setup az időablakban: {self.stats['no_setup']}")
            print(f"Generált Long szignálok: {self.stats['long_signals']}")
            print(f"Generált Short szignálok: {self.stats['short_signals']}")
            print("================================================")
            print("A pénzügyi statisztikákat a cTrader 'Statistics' fülén találod!")
        except Exception as e:
            print(f"Hiba az on_stop-ban: {e}")