using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Text.Json;
using NinjaTrader.Client;

namespace ninja_exec
{
    #region Model Classes
    // IBKR contract details
    public class IbkrContract
    {
        public int contract_id { get; set; }
        public string? symbol { get; set; }
        public string? security_type { get; set; }
        public string? last_trade_date_or_contract_month { get; set; }
        public double strike { get; set; }
        public string? right { get; set; }
        public string? multiplier { get; set; }
        public string? exchange { get; set; }
        public string? currency { get; set; }
        public string? local_symbol { get; set; }
        public string? primary_exchange { get; set; }
        public string? trading_class { get; set; }
        public bool include_expired { get; set; }
        public string? security_id_type { get; set; }
        public string? security_id { get; set; }
        public string? combo_legs_description { get; set; }
        public object[]? combo_legs { get; set; }
        public object? delta_neutral_contract { get; set; }
        public string? issuer_id { get; set; }
        public string? description { get; set; }
    }

    // Target details
    public class Target
    {
        public string? direction { get; set; } // e.g. "Short" or "Long"
        public int qty { get; set; }
        public double? sl { get; set; }        // optional stop loss
    }

    // Candle data
    public class Candle
    {
        public long timestamp { get; set; }
        public string? symbol { get; set; }
        public double open { get; set; }
        public double high { get; set; }
        public double low { get; set; }
        public double close { get; set; }
        public double volume { get; set; }
    }

    // Indicator data
    public class Indicator
    {
        public string? name { get; set; }
        // e.g. "value": [(-1.0, 0), (21994.75, 1735247537), ...]
        public object[]? value { get; set; }
    }

    // Main order request structure
    public class OrderRequest
    {
        public string? symbol { get; set; }
        public IbkrContract? ibkr_contract { get; set; }
        public Target? target { get; set; }
        public Candle[]? bars { get; set; }
        public Indicator[]? indicators { get; set; }
    }
    #endregion

    class Program
    {
        private static Client? myClient;
        private static readonly string account = "Sim101";
        private static readonly int port = 8003; // Adjust as needed

        static async Task Main(string[] args)
        {
            try
            {
                // Initialize NinjaTrader Client
                myClient = new Client();
                if (myClient == null) 
                    throw new InvalidOperationException("Failed to create NinjaTrader client");

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
                    Environment.Exit(0);
                };

                Console.WriteLine($"Server started on port {port}. Press Ctrl+C to shut down.");

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
                        // Listen on all interfaces
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
                                    // 1) Deserialize the incoming JSON
                                    var order = await JsonSerializer.DeserializeAsync<OrderRequest>(
                                        context.Request.Body,
                                        new JsonSerializerOptions
                                        {
                                            PropertyNameCaseInsensitive = true,
                                            AllowTrailingCommas = true
                                        }
                                    );

                                    if (order == null)
                                    {
                                        context.Response.StatusCode = 400; // Bad Request
                                        await context.Response.WriteAsJsonAsync(new 
                                        { 
                                            success = false, 
                                            message = "Failed to parse order request" 
                                        });
                                        return;
                                    }

                                    Console.WriteLine(
                                        $"Order received: Symbol={order.symbol}, Direction={order.target?.direction}, Qty={order.target?.qty}, StopLoss={order.target?.sl}"
                                    );

                                    // 2) Validate the order
                                    var validationResult = ValidateOrder(order);
                                    if (!validationResult.IsValid)
                                    {
                                        context.Response.StatusCode = 400; // Bad Request
                                        await context.Response.WriteAsJsonAsync(new 
                                        { 
                                            success = false, 
                                            message = validationResult.Message 
                                        });
                                        return;
                                    }

                                    if (myClient == null)
                                    {
                                        // If NinjaTrader client wasn't initialized
                                        context.Response.StatusCode = 500; // Internal Server Error
                                        await context.Response.WriteAsJsonAsync(new 
                                        { 
                                            success = false, 
                                            message = "NinjaTrader client not available" 
                                        });
                                        return;
                                    }

                                    // 3) Check current position
                                    // MarketPosition returns:
                                    //  0 => flat, >0 => long, <0 => short
                                    int currentPos = myClient.MarketPosition(order.symbol!, account);
                                    string currentPosStr = NumericPositionToString(currentPos); 
                                    // => "LONG", "SHORT", or "FLAT"

                                    // 4) Compare to requested direction
                                    string requestedDirection = (order.target?.direction ?? "")
                                                               .Trim()
                                                               .ToUpperInvariant(); 
                                    // => "LONG" or "SHORT"

                                    // If current position matches requested, do not trade
                                    if (currentPosStr == requestedDirection)
                                    {
                                        Console.WriteLine($"Already in a {currentPosStr} position for {order.symbol}. No trade executed.");
                                        await context.Response.WriteAsJsonAsync(new
                                        {
                                            success = true,
                                            message = $"No trade needed. Already {currentPosStr} on {order.symbol}."
                                        });
                                        return;
                                    }

                                    // 5) If we are in a different position, flatten first
                                    if (currentPosStr != "FLAT")
                                    {
                                        Console.WriteLine($"Flattening existing {currentPosStr} position for {order.symbol}...");
                                        ZeroPosition(order.symbol!);
                                    }

                                    // 6) Place the new entry order
                                    //    "SHORT" => "SELL"  |  "LONG" => "BUY"
                                    string ntAction = (requestedDirection == "LONG") ? "BUY" : "SELL";
                                    int quantity = order.target!.qty;
                                    Console.WriteLine($"\n###");
                                    Console.WriteLine($"Executing entry order: {ntAction} {quantity} {order.symbol}");
                                    if (order.target.sl.HasValue)
                                    {
                                        Console.WriteLine($"Placing stop-loss at {order.target.sl.Value}");
                                        Console.WriteLine($"###\n");
                                        PlaceOrder(ntAction, quantity, order.symbol!, order.target.sl.Value);
                                    } else {
                                        PlaceOrder(ntAction, quantity, order.symbol!, 0.0);
                                    }


                                    // 7) If a stop-loss is provided, place a protective stop


                                    // 8) Respond with success
                                    await context.Response.WriteAsJsonAsync(new
                                    {
                                        success = true,
                                        message = $"Flattened {currentPosStr} and placed new {requestedDirection} order for {order.symbol}"
                                    });
                                }
                                catch (JsonException ex)
                                {
                                    Console.WriteLine($"JSON parsing error: {ex.Message}");
                                    context.Response.StatusCode = 400; // Bad Request
                                    await context.Response.WriteAsJsonAsync(new 
                                    { 
                                        success = false, 
                                        message = "Invalid JSON format" 
                                    });
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Error processing order: {ex.Message}");
                                    context.Response.StatusCode = 500; // Internal Server Error
                                    await context.Response.WriteAsJsonAsync(new 
                                    { 
                                        success = false, 
                                        message = "Internal server error" 
                                    });
                                }
                            });

                            // Health Check / Default route
                            endpoints.MapGet("/", async context =>
                            {
                                await context.Response.WriteAsync("NinjaTrader Console App is running.");
                            });
                        });
                    });
                });

        /// <summary>
        /// Checks required fields and basic constraints for the order.
        /// </summary>
        private static (bool IsValid, string Message) ValidateOrder(OrderRequest order)
        {
            if (string.IsNullOrEmpty(order.symbol))
            {
                return (false, "Symbol is required");
            }
            if (order.target == null)
            {
                return (false, "Target is required");
            }
            if (string.IsNullOrEmpty(order.target.direction))
            {
                return (false, "Target direction is required");
            }

            // Must be "LONG" or "SHORT"
            string direction = order.target.direction.Trim().ToUpperInvariant();
            if (direction != "LONG" && direction != "SHORT")
            {
                return (false, "Target direction must be LONG or SHORT");
            }

            if (order.target.qty <= 0)
            {
                return (false, "Quantity must be greater than 0");
            }

            return (true, "Valid");
        }

        /// <summary>
        /// Helper method to convert NinjaTrader's numeric MarketPosition
        /// into a string: "LONG", "SHORT", or "FLAT"
        /// </summary>
        private static string NumericPositionToString(int position)
        {
            if (position > 0)  return "LONG";
            if (position < 0)  return "SHORT";
            return "FLAT";
        }

        /// <summary>
        /// Closes any existing positions for the given symbol.
        /// </summary>
        private static void ZeroPosition(string symbol)
        {
            if (myClient == null)
            {
                Console.WriteLine("NinjaTrader client is not initialized.");
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

        /// <summary>
        /// Places an entry order (market) using the NinjaTrader client.
        /// </summary>
        private static void PlaceOrder(string action, int quantity, string symbol, double slPrice)
        {
            if (myClient == null)
            {
                Console.WriteLine("NinjaTrader client is not initialized.");
                return;
            }

            try
            {
                int result = myClient.Command(
                    "PLACE",
                    account,
                    symbol,
                    action,     // "BUY" or "SELL"
                    quantity,
                    "MARKET",   // Market order
                    0.0,
                    slPrice,
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
        /// Places a protective stop-loss order. 
        ///     If we are LONG (action=BUY), we place a SELL STOP below the market.
        ///     If we are SHORT (action=SELL), we place a BUY STOP above the market.
        /// 
        /// Be sure the stopPrice is on the correct side of the current price or it may fill immediately!
        /// </summary>
        private static void PlaceStopLoss(string stopAction, int quantity, string symbol, double stopPrice)
        {
            if (myClient == null)
            {
                Console.WriteLine("NinjaTrader client is not initialized.");
                return;
            }

            try
            {
                int result = myClient.Command(
                    "PLACE",
                    account,
                    symbol,
                    stopAction,       // Opposite side of the entry
                    quantity,
                    "STOP",           // STOP order
                    stopPrice,        // stop price
                    0.0,              // limit price (unused here)
                    "DAY",
                    "",
                    "",
                    "AlgoTrade",
                    ""
                );
                Console.WriteLine($"{DateTime.Now} | Place stop-loss result: {result}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error placing stop-loss order: {ex.Message}");
            }
        }
    }
}
