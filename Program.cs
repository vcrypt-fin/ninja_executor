using NinjaTrader.Client;
using System;
using System.Net;
using System.IO;
using System.Text;
using System.Text.Json;

namespace NinjaTraderConsoleApp
{
    public class OrderRequest
    {
        public string Symbol { get; set; }
        public string[] Target { get; set; }

        // Optional properties to handle extra fields without affecting deserialization
        public object[] Indicators { get; set; }
        public object[] Bars { get; set; }
    }

    class Program
    {
        private static Client? myClient;
        private static string account = "Sim101";
        private static readonly int port = 8003;
        private static HttpListener? _listener;

        static void Main(string[] args)
        {
            try
            {
                myClient = new Client();
                if (myClient == null) throw new InvalidOperationException("Failed to create NinjaTrader client");

                int connect = myClient.Connected(1);
                Console.WriteLine($"{DateTime.Now} | Connected: {connect}");

                Console.CancelKeyPress += (s, e) => {
                    e.Cancel = true;
                    Stop();
                    Environment.Exit(0);
                };

                StartServer();

                // Keep the application running
                Console.WriteLine("Press Ctrl+C to exit...");
                while (true) Thread.Sleep(1000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error: {ex.Message}");
            }
        }

        static void StartServer()
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            Console.WriteLine($"Server started on port {port}");
            Receive();
        }

        static public void Stop()
        {
            _listener?.Stop();
            Console.WriteLine("Server stopped.");
        }

        static private void Receive()
        {
            _listener?.BeginGetContext(new AsyncCallback(ListenerCallback), _listener);
        }

        private static async void ListenerCallback(IAsyncResult result)
        {
            if (_listener?.IsListening != true) return;

            try
            {
                var context = _listener.EndGetContext(result);
                var request = context.Request;
                var response = context.Response;

                if (request.Url?.AbsolutePath == "/set_target" && request.HttpMethod == "POST")
                {
                    using var reader = new StreamReader(request.InputStream);
                    var body = await reader.ReadToEndAsync();
                    Console.WriteLine($"Received body: {body}");

                    try
                    {
                        var options = new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                            AllowTrailingCommas = true
                        };

                        var order = JsonSerializer.Deserialize<OrderRequest>(body, options);
                        if (order == null)
                        {
                            SendJsonResponse(response, new { success = false, message = "Failed to parse order request" });
                            return;
                        }

                        Console.WriteLine($"Order received: Symbol={order.Symbol}, Target={string.Join(",", order.Target ?? Array.Empty<string>())}");

                        if (string.IsNullOrEmpty(order.Symbol))
                        {
                            SendJsonResponse(response, new { success = false, message = "Symbol is required" });
                            return;
                        }

                        if (order.Target == null || order.Target.Length != 2)
                        {
                            SendJsonResponse(response, new { success = false, message = "Target must be an array with 2 elements" });
                            return;
                        }

                        string? action = order.Target[0]?.ToUpperInvariant();
                        string? quantityStr = order.Target[1];

                        if (string.IsNullOrEmpty(action) || string.IsNullOrEmpty(quantityStr))
                        {
                            SendJsonResponse(response, new { success = false, message = "Invalid target values" });
                            return;
                        }

                        if (!int.TryParse(quantityStr, out int quantity) || quantity < 0)
                        {
                            SendJsonResponse(response, new { success = false, message = "Invalid quantity value" });
                            return;
                        }

                        if (action != "LONG" && action != "SHORT")
                        {
                            SendJsonResponse(response, new { success = false, message = "Action must be LONG or SHORT" });
                            return;
                        }

                        // Convert LONG/SHORT to BUY/SELL
                        string ntAction = action == "LONG" ? "BUY" : "SELL";

                        Console.WriteLine($"Executing order: {ntAction} {quantity} {order.Symbol}");

                        ZeroPosition(order.Symbol);
                        PlaceOrder(ntAction, quantity, order.Symbol);

                        SendJsonResponse(response, new { success = true, message = "Order placed" });
                    }
                    catch (JsonException ex)
                    {
                        Console.WriteLine($"JSON parsing error: {ex.Message}");
                        SendJsonResponse(response, new { success = false, message = "Invalid JSON format" });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error processing order: {ex.Message}");
                        SendJsonResponse(response, new { success = false, message = "Internal server error" });
                    }
                }
                else
                {
                    SendJsonResponse(response, new { success = false, message = "Invalid endpoint or method" });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing request: {ex}");
            }
            finally
            {
                Receive(); // Continue listening
            }
        }

        private static void SendJsonResponse(HttpListenerResponse response, object content)
        {
            try
            {
                var json = JsonSerializer.Serialize(content);
                var buffer = Encoding.UTF8.GetBytes(json);

                response.ContentType = "application/json";
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.OutputStream.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending response: {ex.Message}");
            }
        }

        static void PlaceOrder(string action = "SELL", int quantity = 1, string symbol = "")
        {
            if (myClient == null)
            {
                Console.WriteLine("NinjaTrader client is not initialized.");
                return;
            }

            if (string.IsNullOrEmpty(symbol))
            {
                Console.WriteLine("Symbol is null or empty.");
                return;
            }

            try
            {
                int result = myClient.Command(
                    "PLACE",
                    account,
                    symbol,
                    action,
                    quantity,
                    "MARKET",
                    0.0,
                    0.0,
                    "DAY",
                    "",
                    "",
                    "AlgoTrade",
                    ""
                );
                Console.WriteLine($"{DateTime.Now} | Place order result: {result}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error placing order: {ex.Message}");
            }
        }

        static void ZeroPosition(string symbol = "")
        {
            if (myClient == null)
            {
                Console.WriteLine("NinjaTrader client is not initialized.");
                return;
            }

            if (string.IsNullOrEmpty(symbol))
            {
                Console.WriteLine("Symbol is null or empty.");
                return;
            }

            try
            {
                int result = myClient.Command(
                    "CLOSEPOSITION",
                    account,
                    symbol,
                    "",
                    0,
                    "",
                    0.0,
                    0.0,
                    "",
                    "",
                    "",
                    "",
                    ""
                );
                Console.WriteLine($"{DateTime.Now} | Zero position result: {result}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error closing position: {ex.Message}");
            }
        }
    }
}
