# Finam API - Comprehensive Guide

## Overview

The refactored `FinamApi` class provides a robust, production-ready wrapper around the Finam Trade API gRPC services. It includes:

- ✅ Comprehensive error handling for all gRPC status codes
- ✅ Automatic reconnection with exponential backoff
- ✅ Automatic resubscription after reconnection
- ✅ Proper disposal pattern (IDisposable)
- ✅ Input validation for all public methods
- ✅ CancellationToken support for async operations
- ✅ Thread-safe event streaming
- ✅ Connection status monitoring

## Key Features

### 1. Authentication
```csharp
// Initialize with API token
var api = new FinamApi("your-api-token");

// Optional: Custom endpoint
var api = new FinamApi("your-api-token", "https://custom-endpoint.com");
```

### 2. Connection Management

The API automatically manages the event stream connection:
- Starts connection on initialization
- Monitors connection health
- Automatically reconnects on failure with exponential backoff
- Preserves subscriptions across reconnections

```csharp
// Monitor connection status
api.ConnectionStatusChanged += status => 
{
    Console.WriteLine($"Connection status: {status}");
    // Possible values: "Connected", "Disconnected", "Reconnecting (attempt N)...", "Failed"
};
```

### 3. Error Handling

All methods include comprehensive error handling:

```csharp
api.Error += exception => 
{
    Console.WriteLine($"API Error: {exception.Message}");
};

try
{
    var portfolio = await api.GetPortfolioAsync("CLIENT_ID");
}
catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
{
    // Handle authentication errors
}
catch (RpcException ex) when (ex.StatusCode == StatusCode.ResourceExhausted)
{
    // Handle rate limiting
}
```

### 4. Portfolio Operations

```csharp
// Get portfolio with all data
var portfolio = await api.GetPortfolioAsync(
    clientId: "CLIENT_ID",
    includeCurrencies: true,
    includeMaxBuySell: true,
    includeMoney: true,
    includePositions: true
);

// Access portfolio data
foreach (var position in portfolio.Positions)
{
    Console.WriteLine($"{position.SecurityCode}: {position.Balance}");
}
```

### 5. Order Management

#### Place New Order
```csharp
// Limit order
var result = await api.NewOrderAsync(
    clientId: "CLIENT_ID",
    secBoard: "TQBR",
    secCode: "SBER",
    isBuy: true,
    quantity: 10,
    price: 250.50
);

// Market order (no price)
var result = await api.NewOrderAsync(
    clientId: "CLIENT_ID",
    secBoard: "TQBR",
    secCode: "SBER",
    isBuy: true,
    quantity: 10
);

// Order with conditions
var result = await api.NewOrderAsync(
    clientId: "CLIENT_ID",
    secBoard: "TQBR",
    secCode: "SBER",
    isBuy: true,
    quantity: 10,
    price: 250.50,
    useCredit: false,
    property: OrderProperty.PutInQueue,
    condition: new OrderCondition { Type = OrderConditionType.BidOrLast, Price = 250.0 },
    validBefore: new OrderValidBefore { Type = OrderValidBeforeType.TillEndSession }
);
```

#### Get Orders
```csharp
var orders = await api.GetOrdersAsync(
    clientId: "CLIENT_ID",
    includeActive: true,
    includeCanceled: true,
    includeMatched: true
);

foreach (var order in orders.Orders)
{
    Console.WriteLine($"Order {order.TransactionId}: {order.Status}");
}
```

#### Cancel Order
```csharp
var result = await api.CancelOrderAsync(
    clientId: "CLIENT_ID",
    transactionId: 12345
);
```

### 6. Stop Orders

#### Place Stop Order
```csharp
// Stop-loss order
var stopLoss = new StopLoss
{
    ActivationPrice = 240.0,
    Price = 235.0,
    Quantity = new StopQuantity { Value = 10, Units = StopQuantityUnits.Lots }
};

var result = await api.NewStopAsync(
    clientId: "CLIENT_ID",
    secBoard: "TQBR",
    secCode: "SBER",
    isBuy: false,
    stopLoss: stopLoss
);

// Take-profit order
var takeProfit = new TakeProfit
{
    ActivationPrice = 260.0,
    Quantity = new StopQuantity { Value = 10, Units = StopQuantityUnits.Lots }
};

var result = await api.NewStopAsync(
    clientId: "CLIENT_ID",
    secBoard: "TQBR",
    secCode: "SBER",
    isBuy: false,
    takeProfit: takeProfit
);
```

#### Get Stop Orders
```csharp
var stops = await api.GetStopsAsync(
    clientId: "CLIENT_ID",
    includeActive: true,
    includeCanceled: true,
    includeExecuted: true
);
```

#### Cancel Stop Order
```csharp
var result = await api.CancelStopAsync(
    clientId: "CLIENT_ID",
    stopId: 12345
);
```

### 7. Securities

```csharp
// Get all securities
var securities = await api.GetSecuritiesAsync();

// Filter by board
var securities = await api.GetSecuritiesAsync(board: "TQBR");

// Filter by security code
var securities = await api.GetSecuritiesAsync(secCode: "SBER");

foreach (var security in securities.Securities)
{
    Console.WriteLine($"{security.Code} ({security.Board}): {security.ShortName}");
}
```

### 8. Market Data Streaming

#### Subscribe to Order Book
```csharp
await api.SubscribeOrderBookAsync("TQBR", "SBER");

// Handle order book updates
api.EventResponse += evt =>
{
    if (evt.OrderBook != null)
    {
        var ob = evt.OrderBook;
        Console.WriteLine($"Order book for {ob.SecurityCode}:");
        Console.WriteLine($"Best Bid: {ob.Bids.FirstOrDefault()?.Price}");
        Console.WriteLine($"Best Ask: {ob.Asks.FirstOrDefault()?.Price}");
    }
};

// Unsubscribe when done
await api.UnsubscribeOrderBookAsync("TQBR", "SBER");
```

#### Subscribe to Orders and Trades
```csharp
await api.SubscribeOrderTradeAsync(
    clientIds: new[] { "CLIENT_ID" },
    includeOrders: true,
    includeTrades: true
);

// Handle order and trade events
api.EventResponse += evt =>
{
    if (evt.Order != null)
    {
        Console.WriteLine($"Order update: {evt.Order.TransactionId} - {evt.Order.Status}");
    }
    
    if (evt.Trade != null)
    {
        Console.WriteLine($"Trade: {evt.Trade.TradeNo} - {evt.Trade.Quantity} @ {evt.Trade.Price}");
    }
};
```

### 9. Candles (Historical Data)

#### Day Candles
```csharp
var interval = new DayCandleInterval
{
    From = Google.Type.Date.FromDateTime(DateTime.Today.AddDays(-30)),
    To = Google.Type.Date.FromDateTime(DateTime.Today),
    Count = 30
};

var candles = await api.GetDayCandlesAsync(
    secBoard: "TQBR",
    secCode: "SBER",
    timeFrame: DayCandleTimeFrame.D1,
    interval: interval
);

foreach (var candle in candles.Candles)
{
    Console.WriteLine($"{candle.Date}: O={candle.Open} H={candle.High} L={candle.Low} C={candle.Close}");
}
```

#### Intraday Candles
```csharp
var interval = new IntradayCandleInterval
{
    From = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(-5)),
    To = Timestamp.FromDateTime(DateTime.UtcNow),
    Count = 300
};

var candles = await api.GetIntradayCandlesAsync(
    secBoard: "TQBR",
    secCode: "SBER",
    timeFrame: IntradayCandleTimeFrame.M1,
    interval: interval
);
```

### 10. Keep-Alive

```csharp
// Send keep-alive to maintain connection
await api.SendKeepAliveAsync();

// Or use a timer
var timer = new Timer(_ => 
{
    api.SendKeepAliveAsync().Wait();
}, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
```

### 11. Proper Disposal

```csharp
// Use using statement
using (var api = new FinamApi("your-api-token"))
{
    // Use API
    var securities = await api.GetSecuritiesAsync();
}

// Or manual disposal
var api = new FinamApi("your-api-token");
try
{
    // Use API
}
finally
{
    api.Dispose();
}
```

## Error Handling Guide

### Common gRPC Status Codes

| Status Code | Meaning | Recommended Action |
|------------|---------|-------------------|
| `Unauthenticated` | Invalid or expired token | Refresh API token |
| `PermissionDenied` | Insufficient permissions | Check account permissions |
| `ResourceExhausted` | Rate limit exceeded | Implement backoff and retry |
| `Unavailable` | Service temporarily down | Wait and retry (automatic) |
| `DeadlineExceeded` | Request timeout | Retry with longer timeout |
| `InvalidArgument` | Invalid request parameters | Check input validation |

### Error Event Handler

```csharp
api.Error += ex =>
{
    if (ex.Message.Contains("Authentication failed"))
    {
        // Handle authentication errors
    }
    else if (ex.Message.Contains("Rate limit exceeded"))
    {
        // Handle rate limiting
    }
    else if (ex.Message.Contains("Service temporarily unavailable"))
    {
        // Stream will auto-reconnect
    }
};
```

## Reconnection Behavior

The API implements automatic reconnection with exponential backoff:

1. Initial connection fails → Retry after 1 second
2. Second attempt fails → Retry after 2 seconds
3. Third attempt fails → Retry after 4 seconds
4. Continues up to 60 seconds maximum delay
5. After 10 failed attempts, stops retrying

All active subscriptions are automatically restored after successful reconnection.

## Thread Safety

The `FinamApi` class is thread-safe for:
- Event handlers (EventResponse, Error, ConnectionStatusChanged)
- Request ID generation (GetRandomId)
- Concurrent API calls
- Stream writing operations

## Best Practices

1. **Always dispose**: Use `using` statements or manually call `Dispose()`
2. **Handle errors**: Subscribe to the `Error` event for centralized error handling
3. **Monitor connection**: Subscribe to `ConnectionStatusChanged` for connection monitoring
4. **Use CancellationTokens**: Pass CancellationTokens to support operation cancellation
5. **Validate inputs**: The API validates all inputs, but validate on your side too
6. **Rate limiting**: Be aware of API rate limits and implement backoff strategies
7. **Keep-alive**: Send periodic keep-alive requests for long-lived connections

## Example: Complete Integration

```csharp
public class TradingBot : IDisposable
{
    private readonly FinamApi _api;
    private readonly string _clientId;

    public TradingBot(string apiToken, string clientId)
    {
        _clientId = clientId;
        _api = new FinamApi(apiToken);
        
        // Setup event handlers
        _api.EventResponse += OnEvent;
        _api.Error += OnError;
        _api.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    public async Task StartAsync()
    {
        // Get initial portfolio
        var portfolio = await _api.GetPortfolioAsync(_clientId);
        Console.WriteLine($"Initial equity: {portfolio.Equity}");

        // Subscribe to order book for specific securities
        await _api.SubscribeOrderBookAsync("TQBR", "SBER");
        await _api.SubscribeOrderBookAsync("TQBR", "GAZP");

        // Subscribe to order and trade updates
        await _api.SubscribeOrderTradeAsync(new[] { _clientId });
    }

    private void OnEvent(Event evt)
    {
        if (evt.OrderBook != null)
        {
            ProcessOrderBook(evt.OrderBook);
        }
        else if (evt.Order != null)
        {
            ProcessOrder(evt.Order);
        }
        else if (evt.Trade != null)
        {
            ProcessTrade(evt.Trade);
        }
    }

    private void ProcessOrderBook(OrderBookEvent orderBook)
    {
        // Implement your trading logic
    }

    private void ProcessOrder(OrderEvent order)
    {
        Console.WriteLine($"Order {order.TransactionId}: {order.Status}");
    }

    private void ProcessTrade(TradeEvent trade)
    {
        Console.WriteLine($"Trade executed: {trade.TradeNo}");
    }

    private void OnError(Exception ex)
    {
        Console.WriteLine($"API Error: {ex.Message}");
    }

    private void OnConnectionStatusChanged(string status)
    {
        Console.WriteLine($"Connection: {status}");
    }

    public void Dispose()
    {
        _api?.Dispose();
    }
}

// Usage
using (var bot = new TradingBot("your-api-token", "CLIENT_ID"))
{
    await bot.StartAsync();
    Console.WriteLine("Press any key to stop...");
    Console.ReadKey();
}
```

## Testing

The API includes comprehensive unit tests covering:
- Input validation
- Error handling
- Event subscriptions
- Disposal pattern
- Thread safety

Run tests with:
```bash
cd FinamClient.Tests
dotnet test
```

## Version History

### v2.0.0 - Major Refactoring
- Added IDisposable pattern
- Implemented automatic reconnection with exponential backoff
- Added comprehensive error handling for all gRPC status codes
- Added CancellationToken support
- Improved input validation
- Added connection status monitoring
- Added thread-safe event handling
- Improved request ID generation
- Added subscription persistence across reconnections
- Enhanced documentation
