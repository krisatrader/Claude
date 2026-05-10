using System;
using System.Net;
using System.Text;
using System.Threading;
using System.IO;
using System.Text.Json;
using cAlgo.API;
using System.Linq;

namespace cAlgo.Robots
{
    [Robot(AccessRights = AccessRights.FullAccess)]
    public class MCPBridge : Robot
    {
        private HttpListener _listener;
        private Thread _listenerThread;
        private bool _isRunning;

        [Parameter("Port", DefaultValue = 5000)]
        public int Port { get; set; }

        protected override void OnStart()
        {
            _isRunning = true;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            
            try 
            {
                _listener.Start();
                _listenerThread = new Thread(Listen) { IsBackground = true };
                _listenerThread.Start();
                Print($"MCP Bridge Started successfully on http://127.0.0.1:{Port}");
            }
            catch (Exception ex)
            {
                Print($"Failed to start MCP Bridge HTTP server: {ex.Message}. Make sure you have FullAccess permissions.");
                Stop();
            }
        }

        private void Listen()
        {
            while (_isRunning && _listener.IsListening)
            {
                try
                {
                    var context = _listener.GetContext();
                    ProcessRequest(context);
                }
                catch (HttpListenerException) { break; }
                catch (Exception ex) { Print("Listener Error: " + ex); }
            }
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            string responseString = "{}";
            
            try
            {
                string path = request.Url.LocalPath;
                
                if (request.HttpMethod == "GET" && path == "/api/account")
                {
                    var waitHandle = new ManualResetEvent(false);
                    BeginInvokeOnMainThread(() =>
                    {
                        var data = new { 
                            Balance = Account.Balance, 
                            Equity = Account.Equity, 
                            Margin = Account.Margin, 
                            FreeMargin = Account.FreeMargin 
                        };
                        responseString = JsonSerializer.Serialize(data);
                        waitHandle.Set();
                    });
                    waitHandle.WaitOne();
                }
                else if (request.HttpMethod == "GET" && path == "/api/positions")
                {
                    var waitHandle = new ManualResetEvent(false);
                    BeginInvokeOnMainThread(() =>
                    {
                        var list = Positions.Select(p => new {
                            Id = p.Id,
                            Symbol = p.SymbolName,
                            TradeType = p.TradeType.ToString(),
                            Volume = p.VolumeInUnits,
                            EntryPrice = p.EntryPrice,
                            NetProfit = p.NetProfit,
                            Pips = p.Pips
                        }).ToList();
                        responseString = JsonSerializer.Serialize(list);
                        waitHandle.Set();
                    });
                    waitHandle.WaitOne();
                }
                else if (request.HttpMethod == "POST" && path == "/api/execute")
                {
                    using var reader = new StreamReader(request.InputStream);
                    string body = reader.ReadToEnd();
                    var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    
                    string symbol = root.GetProperty("Symbol").GetString();
                    string type = root.GetProperty("TradeType").GetString();
                    double volume = root.GetProperty("Volume").GetDouble();
                    
                    var tradeType = type.Equals("Buy", StringComparison.OrdinalIgnoreCase) ? TradeType.Buy : TradeType.Sell;
                    
                    var waitHandle = new ManualResetEvent(false);
                    string error = null;
                    
                    BeginInvokeOnMainThread(() =>
                    {
                        var res = ExecuteMarketOrder(tradeType, symbol, volume, "MCP_Bot");
                        if (!res.IsSuccessful) error = res.Error.ToString();
                        waitHandle.Set();
                    });
                    waitHandle.WaitOne();
                    
                    responseString = JsonSerializer.Serialize(new { Success = error == null, Error = error });
                }
                else 
                {
                    responseString = JsonSerializer.Serialize(new { Error = "Endpoint not found." });
                    response.StatusCode = 404;
                }
            }
            catch (Exception ex)
            {
                responseString = JsonSerializer.Serialize(new { Error = ex.Message });
                response.StatusCode = 500;
            }

            byte[] buffer = Encoding.UTF8.GetBytes(responseString);
            response.ContentLength64 = buffer.Length;
            response.ContentType = "application/json";
            
            try 
            {
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.OutputStream.Close();
            }
            catch { /* Ignore connection drops */ }
        }

        protected override void OnStop()
        {
            _isRunning = false;
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
        }
    }
}
