using Finam.TradeApi.Proto.V1;
using FinamClient;
using Grpc.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace OrderBookScreener.Connectors
{
    /// <summary>
    /// Коннектор к Finam Trade API
    /// </summary>
    public class FinamConnector : IConnector
    {
        private ConcurrentDictionary<string, Money> _moneys = new();
        public IDictionary<string, Money> Moneys => _moneys;

        private ConcurrentDictionary<string, Position> _positons = new();
        public IDictionary<string, Position> Positions => _positons;

        private ConcurrentDictionary<string, Order> _order = new();
        public IDictionary<string, Order> Orders => _order;

        public event Action? UpdateMoneys;
        public event Action? UpdatePositions;
        public event Action<Order>? UpdateOrder;
        public event Action<OrderBook>? UpdateOrderBook;
        public event Action<FinInfo>? UpdateFinInfo;
        public event Action<Exception>? Error;
        public event Action<string>? Message;

        private readonly FinamApi _client;
        private readonly Config _config;
        private readonly BlockingCollection<Event> _events = new();
        private Timer? _timerUpdateExtraData;
        private Timer? _timerKeepAlive;
        private Timer? _timerResubscribe;
        private bool _isPortfolioAvailable;
        private bool _isConnected;
        private bool _isReconnecting;
        private readonly object _reconnectLock = new();
        private readonly HashSet<string> _subscribedOrderBooks = new();
        private readonly HashSet<string> _subscribedOrderTrades = new();
        private const int KEEP_ALIVE_INTERVAL_SECONDS = 45;
        private const int RESUBSCRIBE_CHECK_INTERVAL_SECONDS = 300; // 5 minutes
        private const int MAX_RECONNECT_ATTEMPTS = 5;
        private const int RECONNECT_DELAY_MS = 5000;

        public FinamConnector(params object[] args)
        {
            try
            {
                _config = (Config)args[0] ?? throw new ArgumentNullException(nameof(args), "Config parameter is required");
                
                if (string.IsNullOrWhiteSpace(_config.Token))
                    throw new ArgumentException("Token is required in config", nameof(_config.Token));
                
                if (string.IsNullOrWhiteSpace(_config.ClientId))
                    throw new ArgumentException("ClientId is required in config", nameof(_config.ClientId));

                Message?.Invoke("Initializing Finam API client...");
                _client = new FinamApi(_config.Token!);
                _client.EventResponse += OnEventResponse;
                
                // Start event processing
                Task.Run(ProcessEvents);
                
                Message?.Invoke("FinamConnector initialized successfully");
            }
            catch (Exception ex)
            {
                Error?.Invoke(new Exception("Failed to initialize FinamConnector", ex));
                throw;
            }
        }

        public async Task Connect()
        {
            if (_isConnected)
            {
                Message?.Invoke("Already connected");
                return;
            }

            try
            {
                Message?.Invoke("Connecting to Finam Trade API...");
                
                // Test authentication by attempting to get securities
                Message?.Invoke("Validating authentication...");
                var securities = await _client.GetSecuritiesAsync().ConfigureAwait(false);
                Message?.Invoke($"Authentication successful. Found {securities.Securities.Count} securities");

                // Check portfolio access
                Message?.Invoke("Checking portfolio access...");
                await CheckPortfolioAccessAsync().ConfigureAwait(false);

                // Get initial orders if portfolio is available
                if (_isPortfolioAvailable)
                {
                    Message?.Invoke("Retrieving initial orders...");
                    await UpdateOrdersAsync().ConfigureAwait(false);
                }

                // Subscribe to order and trade events
                Message?.Invoke("Subscribing to order and trade events...");
                await _client.SubscribeOrderTradeAsync(new[] { _config.ClientId }).ConfigureAwait(false);
                _subscribedOrderTrades.Add(_config.ClientId);

                // Subscribe to order books for key markets
                Message?.Invoke("Subscribing to order books...");
                await SubscribeToOrderBooksAsync(securities).ConfigureAwait(false);

                // Start maintenance timers
                StartMaintenanceTimers();

                _isConnected = true;
                Message?.Invoke("Successfully connected to Finam Trade API");
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
            {
                var authEx = new Exception("Authentication failed: Invalid or expired token", ex);
                Error?.Invoke(authEx);
                Message?.Invoke("Authentication failed. Please check your token.");
                throw authEx;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
            {
                var permEx = new Exception("Permission denied: Insufficient privileges", ex);
                Error?.Invoke(permEx);
                Message?.Invoke("Permission denied. Check token permissions.");
                throw permEx;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
            {
                var connEx = new Exception("Connection failed: Service unavailable", ex);
                Error?.Invoke(connEx);
                Message?.Invoke("Connection failed: Service is temporarily unavailable.");
                throw connEx;
            }
            catch (Exception ex)
            {
                var connEx = new Exception("Failed to connect to Finam Trade API", ex);
                Error?.Invoke(connEx);
                throw connEx;
            }
        }

        public void Dispose()
        {
            try
            {
                Message?.Invoke("Disposing FinamConnector...");
                
                _isConnected = false;
                
                // Stop timers
                _timerUpdateExtraData?.Dispose();
                _timerKeepAlive?.Dispose();
                _timerResubscribe?.Dispose();
                
                // Clear collections
                _moneys.Clear();
                _positons.Clear();
                _order.Clear();
                _subscribedOrderBooks.Clear();
                _subscribedOrderTrades.Clear();
                
                // Complete the events collection to stop processing
                _events.Complete();
                
                Message?.Invoke("FinamConnector disposed successfully");
            }
            catch (Exception ex)
            {
                Error?.Invoke(new Exception("Error during disposal", ex));
            }
        }

        private async Task UpdatePortfolioAsync()
        {
            try
            {
                Message?.Invoke("Updating portfolio data...");
                var portfolio = await _client.GetPortfolioAsync(_config.ClientId).ConfigureAwait(false);
                
                // Update currencies
                var updatedMoneys = new ConcurrentDictionary<string, Money>();
                foreach (var item in portfolio.Currencies)
                {
                    var money = new Money
                    {
                        Currency = item.Name,
                        Balance = item.Balance.Normalize(),
                    };
                    if (money.Balance >= 0) // Include zero balance for completeness
                    {
                        updatedMoneys[money.Currency] = money;
                    }
                }
                
                // Update positions
                var updatedPositions = new ConcurrentDictionary<string, Position>();
                foreach (var item in portfolio.Positions)
                {
                    var position = new Position
                    {
                        Symbol = item.SecurityCode,
                        Market = item.Market.ToString(),
                        Balance = item.Balance,
                        Profit = item.Profit.Normalize(),
                    };
                    updatedPositions[position.Symbol] = position;
                }
                
                // Atomic update
                _moneys = updatedMoneys;
                _positons = updatedPositions;
                
                UpdateMoneys?.Invoke();
                UpdatePositions?.Invoke();
                
                Message?.Invoke($"Portfolio updated: {_moneys.Count} currencies, {_positons.Count} positions");
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
            {
                var authEx = new Exception("Portfolio update failed: Authentication expired", ex);
                Error?.Invoke(authEx);
                Message?.Invoke("Authentication expired during portfolio update");
                await HandleReconnectionAsync().ConfigureAwait(false);
                throw authEx;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
            {
                var permEx = new Exception("Portfolio update failed: Permission denied", ex);
                Error?.Invoke(permEx);
                Message?.Invoke("Permission denied for portfolio access");
                throw permEx;
            }
            catch (Exception ex)
            {
                var updateEx = new Exception("Failed to update portfolio", ex);
                Error?.Invoke(updateEx);
                Message?.Invoke($"Portfolio update failed: {ex.Message}");
                throw updateEx;
            }
        }

        private async Task UpdateOrdersAsync()
        {
            try
            {
                Message?.Invoke("Updating orders data...");
                var orders = await _client.GetOrdersAsync(_config!.ClientId!).ConfigureAwait(false);

                var updatedOrders = new ConcurrentDictionary<string, Order>();
                foreach (var item in orders.Orders)
                {
                    var order = new Order
                    {
                        Id = item.TransactionId.ToString(),
                        Date = (item.CreatedAt ?? item.AcceptedAt)?.ToDateTime().ToLocalTime() ?? DateTime.Now,
                        Symbol = item.SecurityCode,
                        Status = ToOrderStatus(item.Status),
                        Side = ToOrderSide(item.BuySell),
                        Price = item.Price,
                        Quantity = item.Quantity,
                        RestQuantity = item.Balance,
                    };
                    updatedOrders[order.Id] = order;
                }
                
                // Atomic update
                _order = updatedOrders;
                
                // Notify for each order
                foreach (var order in _order.Values)
                {
                    UpdateOrder?.Invoke(order);
                }
                
                Message?.Invoke($"Orders updated: {_order.Count} orders");
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
            {
                var authEx = new Exception("Orders update failed: Authentication expired", ex);
                Error?.Invoke(authEx);
                Message?.Invoke("Authentication expired during orders update");
                await HandleReconnectionAsync().ConfigureAwait(false);
                throw authEx;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
            {
                var permEx = new Exception("Orders update failed: Permission denied", ex);
                Error?.Invoke(permEx);
                Message?.Invoke("Permission denied for orders access");
                throw permEx;
            }
            catch (Exception ex)
            {
                var updateEx = new Exception("Failed to update orders", ex);
                Error?.Invoke(updateEx);
                Message?.Invoke($"Orders update failed: {ex.Message}");
                throw updateEx;
            }
        }

        public async Task SendOrderAsync(string account, string board, string symbol, bool isBuy, double quantity, double price)
        {
            if (!_isPortfolioAvailable)
            {
                Message?.Invoke("Portfolio access not available for order operations");
                return;
            }

            try
            {
                Message?.Invoke($"Sending { (isBuy ? "BUY" : "SELL") } order: {quantity} {symbol} @ {price}");
                
                var result = await _client.NewOrderAsync(account, board, symbol, isBuy, (int)quantity, price).ConfigureAwait(false);
                
                Message?.Invoke($"Order sent successfully. Transaction ID: {result.TransactionId}");
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
            {
                var authEx = new Exception("Order failed: Authentication expired", ex);
                Error?.Invoke(authEx);
                Message?.Invoke("Authentication expired while sending order");
                await HandleReconnectionAsync().ConfigureAwait(false);
                throw authEx;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.ResourceExhausted)
            {
                var rateEx = new Exception("Order failed: Rate limit exceeded", ex);
                Error?.Invoke(rateEx);
                Message?.Invoke("Rate limit exceeded. Please wait before sending more orders.");
                throw rateEx;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition)
            {
                var preEx = new Exception("Order failed: Precondition failed (insufficient funds, market closed, etc.)", ex);
                Error?.Invoke(preEx);
                Message?.Invoke($"Order rejected: {ex.Status.Detail}");
                throw preEx;
            }
            catch (Exception ex)
            {
                var orderEx = new Exception($"Failed to send order for {symbol}", ex);
                Error?.Invoke(orderEx);
                Message?.Invoke($"Order failed: {ex.Message}");
                throw orderEx;
            }
        }

        public async Task CancelOrderAsync(Order order)
        {
            if (!_isPortfolioAvailable)
            {
                Message?.Invoke("Portfolio access not available for order operations");
                return;
            }

            try
            {
                Message?.Invoke($"Cancelling order {order.Id} for {order.Symbol}");
                
                var result = await _client.CancelOrderAsync(_config!.ClientId!, int.Parse(order.Id)).ConfigureAwait(false);
                
                Message?.Invoke($"Order cancellation successful. Transaction ID: {result.TransactionId}");
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
            {
                var authEx = new Exception("Order cancellation failed: Authentication expired", ex);
                Error?.Invoke(authEx);
                Message?.Invoke("Authentication expired while cancelling order");
                await HandleReconnectionAsync().ConfigureAwait(false);
                throw authEx;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                var notFoundEx = new Exception($"Order {order.Id} not found or already cancelled", ex);
                Error?.Invoke(notFoundEx);
                Message?.Invoke($"Order {order.Id} not found or already cancelled");
                throw notFoundEx;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition)
            {
                var preEx = new Exception($"Order {order.Id} cannot be cancelled (already filled, etc.)", ex);
                Error?.Invoke(preEx);
                Message?.Invoke($"Order {order.Id} cannot be cancelled: {ex.Status.Detail}");
                throw preEx;
            }
            catch (Exception ex)
            {
                var cancelEx = new Exception($"Failed to cancel order {order.Id}", ex);
                Error?.Invoke(cancelEx);
                Message?.Invoke($"Order cancellation failed: {ex.Message}");
                throw cancelEx;
            }
        }

        private void ProcessEvents()
        {
            try
            {
                foreach (var ev in _events.GetConsumingEnumerable())
                {
                    try
                    {
                        ProcessSingleEvent(ev);
                    }
                    catch (Exception ex)
                    {
                        Error?.Invoke(new Exception($"Error processing event: {ev}", ex));
                        Message?.Invoke($"Event processing error: {ex.Message}");
                    }
                }
            }
            catch (InvalidOperationException) when (_events.IsAddingCompleted)
            {
                // Expected when the collection is completed during disposal
                Message?.Invoke("Event processing stopped (collection completed)");
            }
            catch (Exception ex)
            {
                Error?.Invoke(new Exception("Critical error in event processing loop", ex));
                Message?.Invoke($"Event processing loop error: {ex.Message}");
            }
        }

        private void ProcessSingleEvent(Event ev)
        {
            if (ev.OrderBook != null)
            {
                ProcessOrderBookEvent(ev.OrderBook);
            }

            if (ev.Order != null)
            {
                ProcessOrderEvent(ev.Order);
            }

            if (ev.Trade != null)
            {
                ProcessTradeEvent(ev.Trade);
            }

            if (ev.Portfolio != null)
            {
                ProcessPortfolioEvent(ev.Portfolio);
            }

            if (ev.Response != null)
            {
                ProcessResponseEvent(ev.Response);
            }
        }

        private void ProcessOrderBookEvent(OrderBookEvent orderBookEvent)
        {
            try
            {
                var key = $"{orderBookEvent.SecurityBoard}:{orderBookEvent.SecurityCode}";
                
                var orderBook = new OrderBook
                {
                    SecBoard = orderBookEvent.SecurityBoard,
                    SecCode = orderBookEvent.SecurityCode,
                    Bids = orderBookEvent.Bids.Select(x => new OrderBookRow(true, x.Price, x.Quantity)).ToArray(),
                    Asks = orderBookEvent.Asks.Select(x => new OrderBookRow(false, x.Price, x.Quantity)).ToArray(),
                };
                
                UpdateOrderBook?.Invoke(orderBook);
                
                // Track subscription
                _subscribedOrderBooks.Add(key);
            }
            catch (Exception ex)
            {
                Error?.Invoke(new Exception($"Error processing order book event for {orderBookEvent.SecurityBoard}:{orderBookEvent.SecurityCode}", ex));
            }
        }

        private void ProcessOrderEvent(OrderEvent orderEvent)
        {
            try
            {
                var order = new Order
                {
                    Id = orderEvent.TransactionId.ToString(),
                    Date = (orderEvent.CreatedAt ?? orderEvent.AcceptedAt)?.ToDateTime().ToLocalTime() ?? DateTime.Now,
                    Symbol = orderEvent.SecurityCode,
                    Status = ToOrderStatus(orderEvent.Status),
                    Side = ToOrderSide(orderEvent.BuySell),
                    Price = orderEvent.Price,
                    Quantity = orderEvent.Quantity,
                    RestQuantity = orderEvent.Balance,
                };

                _order[order.Id] = order;
                UpdateOrder?.Invoke(order);
                
                Message?.Invoke($"Order updated: {order.Id} - {order.Status} {order.Side} {order.Symbol}");
            }
            catch (Exception ex)
            {
                Error?.Invoke(new Exception($"Error processing order event for transaction {orderEvent.TransactionId}", ex));
            }
        }

        private void ProcessTradeEvent(TradeEvent tradeEvent)
        {
            try
            {
                Message?.Invoke($"Trade received: {tradeEvent.SecurityCode} - {tradeEvent.Quantity} @ {tradeEvent.Price}");
                
                // Update portfolio asynchronously after trade
                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(1000); // Small delay to allow backend to update
                        await UpdatePortfolioAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Error?.Invoke(new Exception($"Failed to update portfolio after trade {tradeEvent.TradeNo}", ex));
                    }
                });
            }
            catch (Exception ex)
            {
                Error?.Invoke(new Exception($"Error processing trade event for trade {tradeEvent.TradeNo}", ex));
            }
        }

        private void ProcessPortfolioEvent(PortfolioEvent portfolioEvent)
        {
            try
            {
                Message?.Invoke($"Portfolio event received for client {portfolioEvent.ClientId}");
                
                // Update portfolio data from event
                Task.Run(async () =>
                {
                    try
                    {
                        await UpdatePortfolioAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Error?.Invoke(new Exception($"Failed to update portfolio from event for client {portfolioEvent.ClientId}", ex));
                    }
                });
            }
            catch (Exception ex)
            {
                Error?.Invoke(new Exception($"Error processing portfolio event for client {portfolioEvent.ClientId}", ex));
            }
        }

        private void ProcessResponseEvent(ResponseEvent responseEvent)
        {
            try
            {
                Message?.Invoke($"Response event received: {responseEvent.RequestId} - Success: {responseEvent.Success}");
                
                if (!responseEvent.Success)
                {
                    var errorMessages = responseEvent.Errors?.Select(e => e.Message).ToArray() ?? new string[0];
                    var errorMessage = string.Join("; ", errorMessages);
                    var errorEx = new Exception($"API Error for request {responseEvent.RequestId}: {errorMessage}");
                    Error?.Invoke(errorEx);
                    Message?.Invoke($"API Error: {errorMessage}");
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke(new Exception($"Error processing response event for request {responseEvent.RequestId}", ex));
            }
        }

        private async Task UpdateExtraData()
        {
            try
            {
                var client = new HttpClient();
                var baseUrl = "https://iss.moex.com/iss/engines";
                var pameteres = "?iss.meta=off&iss.only=marketdata&marketdata.columns=BOARDID,SECID,VALTODAY,VOLTODAY";
                await UpdateByUrl($"{baseUrl}/stock/markets/shares/boards/TQBR/securities.xml{pameteres}");
                await UpdateByUrl($"{baseUrl}/currency/markets/selt/boards/CETS/securities.xml{pameteres}");
                await UpdateByUrl($"{baseUrl}/futures/markets/forts/boards/RFUD/securities.xml{pameteres}");

                async Task UpdateByUrl(string url)
                {
                    var res = await client.GetStringAsync(url).ConfigureAwait(false);

                    var xDoc = new XmlDocument();
                    xDoc.LoadXml(res);
                    var rows = xDoc.SelectNodes("//row");
                    if (rows == null)
                        return;
                    foreach (XmlElement row in rows)
                    {
                        var boardid = row.GetAttribute("BOARDID").Replace("RFUD", "FUT");
                        var secid = row.GetAttribute("SECID");
                        var valtoday = row.GetAttribute("VALTODAY");
                        var voltoday = row.GetAttribute("VOLTODAY");

                        if (!double.TryParse(valtoday, out var nValtoday))
                            continue;

                        if (!double.TryParse(voltoday, out var nVoltoday))
                            continue;

                        var finInfo = new FinInfo
                        {
                            Board = boardid,
                            Symbol = secid,
                            Valtoday = nValtoday,
                            Voltoday = nVoltoday,
                        };
                        UpdateFinInfo?.Invoke(finInfo);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        private async Task CheckPortfolioAccessAsync()
        {
            try
            {
                await UpdatePortfolioAsync().ConfigureAwait(false);
                _isPortfolioAvailable = true;
                Message?.Invoke("Portfolio access confirmed");
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
            {
                _isPortfolioAvailable = false;
                Message?.Invoke("Portfolio access not available - continuing without portfolio features");
            }
            catch (Exception ex)
            {
                Message?.Invoke($"Portfolio access check failed: {ex.Message}");
                throw;
            }
        }

        private async Task SubscribeToOrderBooksAsync(GetSecuritiesResult securities)
        {
            try
            {
                var listTQBR = securities.Securities.Where(x => x.Board == "TQBR").ToList();
                var listFUT = securities.Securities.Where(x => x.Board == "FUT").ToList();
                var listCETS = securities.Securities.Where(x => x.Board == "CETS").ToList();
                var listAll = listTQBR.Union(listFUT).Union(listCETS).ToList();

                Message?.Invoke($"Subscribing to {listAll.Count} order books...");
                
                int subscribedCount = 0;
                foreach (var item in listAll)
                {
                    try
                    {
                        await _client.SubscribeOrderBookAsync(item.Board, item.Code).ConfigureAwait(false);
                        _subscribedOrderBooks.Add($"{item.Board}:{item.Code}");
                        subscribedCount++;
                        
                        // Small delay to avoid rate limiting
                        if (subscribedCount % 10 == 0)
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Error?.Invoke(new Exception($"Failed to subscribe to order book {item.Board}:{item.Code}", ex));
                        Message?.Invoke($"Failed to subscribe to {item.Board}:{item.Code} - {ex.Message}");
                    }
                }
                
                Message?.Invoke($"Successfully subscribed to {subscribedCount}/{listAll.Count} order books");
            }
            catch (Exception ex)
            {
                Error?.Invoke(new Exception("Failed to subscribe to order books", ex));
                throw;
            }
        }

        private void StartMaintenanceTimers()
        {
            try
            {
                // Keep-alive timer
                _timerKeepAlive = new Timer(async _ => await SendKeepAliveAsync(), null, 
                    TimeSpan.FromSeconds(KEEP_ALIVE_INTERVAL_SECONDS), 
                    TimeSpan.FromSeconds(KEEP_ALIVE_INTERVAL_SECONDS));

                // Resubscription check timer
                _timerResubscribe = new Timer(async _ => await CheckAndResubscribeAsync(), null,
                    TimeSpan.FromSeconds(RESUBSCRIBE_CHECK_INTERVAL_SECONDS),
                    TimeSpan.FromSeconds(RESUBSCRIBE_CHECK_INTERVAL_SECONDS));

                // Extra data update timer (existing functionality)
                _timerUpdateExtraData = new Timer(async _ => await Task.Run(UpdateExtraData), null,
                    TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));

                Message?.Invoke("Maintenance timers started");
            }
            catch (Exception ex)
            {
                Error?.Invoke(new Exception("Failed to start maintenance timers", ex));
            }
        }

        private async Task SendKeepAliveAsync()
        {
            try
            {
                if (!_isConnected) return;
                
                await _client.SendKeepAliveAsync().ConfigureAwait(false);
                Message?.Invoke("Keep-alive sent");
            }
            catch (Exception ex)
            {
                Error?.Invoke(new Exception("Failed to send keep-alive", ex));
                await HandleReconnectionAsync().ConfigureAwait(false);
            }
        }

        private async Task CheckAndResubscribeAsync()
        {
            try
            {
                if (!_isConnected) return;
                
                Message?.Invoke("Checking subscription health...");
                
                // Check order/trade subscription
                if (!_subscribedOrderTrades.Contains(_config.ClientId))
                {
                    Message?.Invoke("Resubscribing to order/trade events...");
                    await _client.SubscribeOrderTradeAsync(new[] { _config.ClientId }).ConfigureAwait(false);
                    _subscribedOrderTrades.Add(_config.ClientId);
                }
                
                Message?.Invoke("Subscription health check completed");
            }
            catch (Exception ex)
            {
                Error?.Invoke(new Exception("Failed to check/resubscribe", ex));
                await HandleReconnectionAsync().ConfigureAwait(false);
            }
        }

        private async Task HandleReconnectionAsync()
        {
            if (_isReconnecting)
            {
                Message?.Invoke("Reconnection already in progress");
                return;
            }

            lock (_reconnectLock)
            {
                if (_isReconnecting) return;
                _isReconnecting = true;
            }

            try
            {
                Message?.Invoke("Starting reconnection process...");
                
                for (int attempt = 1; attempt <= MAX_RECONNECT_ATTEMPTS; attempt++)
                {
                    try
                    {
                        Message?.Invoke($"Reconnection attempt {attempt}/{MAX_RECONNECT_ATTEMPTS}");
                        
                        // Reset connection state
                        _isConnected = false;
                        _subscribedOrderBooks.Clear();
                        _subscribedOrderTrades.Clear();
                        
                        // Stop timers
                        _timerKeepAlive?.Dispose();
                        _timerResubscribe?.Dispose();
                        
                        // Wait before attempting reconnection
                        if (attempt > 1)
                        {
                            await Task.Delay(RECONNECT_DELAY_MS * attempt).ConfigureAwait(false);
                        }
                        
                        // Attempt to reconnect
                        await Connect().ConfigureAwait(false);
                        
                        Message?.Invoke("Reconnection successful");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Message?.Invoke($"Reconnection attempt {attempt} failed: {ex.Message}");
                        if (attempt == MAX_RECONNECT_ATTEMPTS)
                        {
                            Error?.Invoke(new Exception($"Failed to reconnect after {MAX_RECONNECT_ATTEMPTS} attempts", ex));
                        }
                    }
                }
            }
            finally
            {
                _isReconnecting = false;
            }
        }

        private void OnEventResponse(Event ev)
        {
            try
            {
                if (!_events.IsAddingCompleted)
                {
                    _events.Add(ev);
                }
            }
            catch (InvalidOperationException)
            {
                // Collection is completed, ignore
            }
            catch (Exception ex)
            {
                Error?.Invoke(new Exception("Failed to add event to processing queue", ex));
            }
        }

        private static OrderSide ToOrderSide(Finam.TradeApi.Proto.V1.BuySell side)
        {
            return side == BuySell.Buy ? OrderSide.Buy : OrderSide.Sell;
        }

        private static OrderStatus ToOrderStatus(Finam.TradeApi.Proto.V1.OrderStatus status)
        {
            return status switch
            {
                Finam.TradeApi.Proto.V1.OrderStatus.Unspecified => OrderStatus.None,
                Finam.TradeApi.Proto.V1.OrderStatus.None => OrderStatus.None,
                Finam.TradeApi.Proto.V1.OrderStatus.Active => OrderStatus.Active,
                Finam.TradeApi.Proto.V1.OrderStatus.Cancelled => OrderStatus.Cancelled,
                Finam.TradeApi.Proto.V1.OrderStatus.Matched => OrderStatus.Executed,
                _ => OrderStatus.None,
            };
        }
    }
}
