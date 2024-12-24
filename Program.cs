using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Text.Json;
using NinjaTrader.Client;
using System.Threading.Tasks;

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

        static async Task Main(string[] args)
        {
            try
            {
                // Initialize NinjaTrader Client
                myClient = new Client();
                if (myClient == null) throw new InvalidOperationException("Failed to create NinjaTrader client");

                int connect = myClient.Connected(1);
                Console.WriteLine($"{DateTime.Now} | Connected: {connect}");

                // Handle graceful shutdown
                using IHost host = CreateHostBuilder(args).Build();

                // Register a callback to handle Ctrl+C
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    Console.WriteLine("Shutting down...");
                    host.StopAsync().Wait();
                };

                Console.WriteLine("Server started. Press Ctrl+C to shut down.");

                await host.RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error: {ex.Message}");
            }
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseKestrel(options =>
                    {
                        // Configure Kestrel to listen on all network interfaces
                        // This is important for Docker container networking
                        options.ListenAnyIP(port, listenOptions =>
                        {
                            listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
                        });
                    });
                    webBuilder.Configure(app =>
                    {
                        app.UseRouting();

                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapPost("/set_target", async context =>
                            {
                                try
                                {
                                    // Deserialize the incoming JSON request
                                    var order = await JsonSerializer.DeserializeAsync<OrderRequest>(context.Request.Body, new JsonSerializerOptions
                                    {
                                        PropertyNameCaseInsensitive = true,
                                        AllowTrailingCommas = true
                                    });

                                    if (order == null)
                                    {
                                        context.Response.StatusCode = 400; // Bad Request
                                        await context.Response.WriteAsJsonAsync(new { success = false, message = "Failed to parse order request" });
                                        return;
                                    }

                                    Console.WriteLine($"Order received: Symbol={order.Symbol}, Target={string.Join(",", order.Target ?? Array.Empty<string>())}");

                                    // Validate the order
                                    var validationResult = ValidateOrder(order);
                                    if (!validationResult.IsValid)
                                    {
                                        context.Response.StatusCode = 400; // Bad Request
                                        await context.Response.WriteAsJsonAsync(new { success = false, message = validationResult.Message });
                                        return;
                                    }

                                    // Process the order
                                    string ntAction = order.Target[0].ToUpperInvariant() == "LONG" ? "BUY" : "SELL";
                                    int quantity = int.Parse(order.Target[1]);

                                    Console.WriteLine($"Executing order: {ntAction} {quantity} {order.Symbol}");

                                    ZeroPosition(order.Symbol);
                                    PlaceOrder(ntAction, quantity, order.Symbol);

                                    // Respond with success
                                    await context.Response.WriteAsJsonAsync(new { success = true, message = "Order placed" });
                                }
                                catch (JsonException ex)
                                {
                                    Console.WriteLine($"JSON parsing error: {ex.Message}");
                                    context.Response.StatusCode = 400; // Bad Request
                                    await context.Response.WriteAsJsonAsync(new { success = false, message = "Invalid JSON format" });
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Error processing order: {ex.Message}");
                                    context.Response.StatusCode = 500; // Internal Server Error
                                    await context.Response.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                                }
                            });

                            // Optional: Health Check or Default Route
                            endpoints.MapGet("/", async context =>
                            {
                                await context.Response.WriteAsync("NinjaTrader Console App is running.");
                            });
                        });
                    });
                });

        /// <summary>
        /// Validates the incoming order request.
        /// </summary>
        /// <param name="order">The order request to validate.</param>
        /// <returns>A tuple indicating whether the order is valid and an accompanying message.</returns>
        private static (bool IsValid, string Message) ValidateOrder(OrderRequest order)
        {
            if (string.IsNullOrEmpty(order.Symbol))
            {
                return (false, "Symbol is required");
            }

            if (order.Target == null || order.Target.Length != 2)
            {
                return (false, "Target must be an array with 2 elements");
            }

            string? action = order.Target[0]?.ToUpperInvariant();
            string? quantityStr = order.Target[1];

            if (string.IsNullOrEmpty(action) || string.IsNullOrEmpty(quantityStr))
            {
                return (false, "Invalid target values");
            }

            if (!int.TryParse(quantityStr, out int quantity) || quantity < 0)
            {
                return (false, "Invalid quantity value");
            }

            if (action != "LONG" && action != "SHORT")
            {
                return (false, "Action must be LONG or SHORT");
            }

            return (true, "Valid");
        }

        /// <summary>
        /// Sends a JSON response to the client.
        /// </summary>
        /// <param name="context">The HTTP context.</param>
        /// <param name="content">The content to serialize as JSON.</param>
        private static async Task SendJsonResponse(HttpContext context, object content)
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(content);
        }

        /// <summary>
        /// Places an order using the NinjaTrader client.
        /// </summary>
        /// <param name="action">The action to perform (BUY/SELL).</param>
        /// <param name="quantity">The quantity of the order.</param>
        /// <param name="symbol">The symbol to trade.</param>
        private static void PlaceOrder(string action = "SELL", int quantity = 1, string symbol = "")
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

        /// <summary>
        /// Closes any existing positions for the given symbol.
        /// </summary>
        /// <param name="symbol">The symbol to close positions for.</param>
        private static void ZeroPosition(string symbol = "")
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
