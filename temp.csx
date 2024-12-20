using NinjaTrader.Client;
using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Windows;

namespace NinjaTraderConsoleApp
{
    public class OrderRequest
    {
        public required string Symbol { get; set; }
        public required JsonElement[] Target { get; set; }
        public required JsonElement[] Bars { get; set; }
        public required JsonElement[] Indicators { get; set; }
    }

    class Program
    {
        private static Client? myClient;
        private static HttpListener? listener;
        private static readonly string url = "http://127.0.0.1:8001/";
        private static readonly CancellationTokenSource cancelSource = new();
        private static Task? serverTask;

        [STAThread]
        static async Task Main(string[] args)
        {
            // Handle Ctrl+C gracefully
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("Shutting down server...");
                cancelSource.Cancel();
            };

            // Initialize WPF Application
            var app = new Application();
            app.DispatcherUnhandledException += (sender, e) =>
            {
                Console.WriteLine($"Unhandled exception: {e.Exception}");
                e.Handled = true;
            };

            // Initialize Client
            InitializeClient();

            // Start the HTTP server
            serverTask = StartServer(cancelSource.Token);

            // Run the WPF application on the main thread
            var wpfTask = Task.Run(() => app.Run(), cancelSource.Token);

            // Wait for either the server to stop or the WPF app to exit
            await Task.WhenAny(serverTask, wpfTask);

            // Initiate shutdown
            cancelSource.Cancel();

            // Stop the listener if it's still running
            if (listener != null && listener.IsListening)
            {
                listener.Stop();
                listener.Close();
            }

            // Ensure the WPF application shuts down
            if (app != null)
            {
                app.Dispatcher.Invoke(() => app.Shutdown());
            }

            // Await the server task to complete
            await serverTask;

            Console.WriteLine("Server stopped.");
        }

        private static void InitializeClient()
        {
            myClient = new Client();
            if (myClient == null) throw new InvalidOperationException("Failed to create NinjaTrader client");

            int connect = myClient.Connected(1);
            Console.WriteLine($"{DateTime.Now} | Connected: {connect}");
        }

        private static async Task StartServer(CancellationToken token)
        {
            listener = new HttpListener();
            if (listener == null) throw new InvalidOperationException("Failed to create HTTP listener");

            listener.Prefixes.Add(url);
            listener.Start();
            Console.WriteLine($"{DateTime.Now} | Server listening on {url}");

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var getContextTask = listener.GetContextAsync();

                    var completedTask = await Task.WhenAny(getContextTask, Task.Delay(1000, token));

                    if (completedTask == getContextTask)
                    {
                        var context = await getContextTask;
                        _ = HandleRequest(context);
                    }
                }
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 995) // IO operation aborted
            {
                // Listener was stopped, no action needed
            }
            catch (OperationCanceledException)
            {
                // Cancellation requested, no action needed
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Server error: {ex.Message}");
            }
            finally
            {
                if (listener != null && listener.IsListening)
                {
                    listener.Stop();
                    listener.Close();
                }
            }
        }

        private static async Task HandleRequest(HttpListenerContext context)
        {
            try
            {
                if (myClient == null)
                {
                    await SendResponse(context, 500, new { success = false, message = "NinjaTrader client not initialized" });
                    return;
                }

                if (context.Request.HttpMethod != "POST" || context.Request.Url.LocalPath != "/set_target")
                {
                    await SendResponse(context, 404, new { success = false, message = "Not found" });
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                Console.WriteLine($"Received request body: {body}"); // Debug log

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true
                };

                var order = JsonSerializer.Deserialize<OrderRequest>(body, options);

                // Validate order
                if (order == null || string.IsNullOrEmpty(order.Symbol))
                {
                    await SendResponse(context, 400, new { success = false, message = "Missing symbol" });
                    return;
                }

                if (order.Target == null || order.Target.Length != 2)
                {
                    await SendResponse(context, 400, new { success = false, message = "Invalid target array" });
                    return;
                }

                // Parse target values
                string direction = order.Target[0].GetString();
                if (!int.TryParse(order.Target[1].GetString(), out int quantity))
                {
                    await SendResponse(context, 400, new { success = false, message = "Invalid quantity format" });
                    return;
                }

                Console.WriteLine($"Placing order: {direction} {quantity} {order.Symbol}"); // Debug log

                // Place order with NinjaTrader
                int result = myClient.Command(
                    "PLACE_MARKET_ORDER",
                    "SIM101",
                    order.Symbol,
                    direction,
                    quantity,
                    "Market",
                    0.0,
                    0.0,
                    "DAY",
                    "",
                    "",
                    "AlgoTrade",
                    ""
                );

                if (result == 0)
                {
                    await SendResponse(context, 200, new { success = true, message = "Order placed successfully" });
                }
                else
                {
                    await SendResponse(context, 500, new { success = false, message = "Failed to place order" });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing request: {ex}"); // Debug log
                await SendResponse(context, 500, new { success = false, message = ex.Message });
            }
        }

        private static async Task SendResponse(HttpListenerContext context, int statusCode, object content)
        {
            try
            {
                var response = context.Response;
                response.StatusCode = statusCode;
                response.ContentType = "application/json";

                var json = JsonSerializer.Serialize(content);
                var buffer = Encoding.UTF8.GetBytes(json);

                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                response.OutputStream.Close();

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending response: {ex.Message}");
            }
        }
    }
}
