using Finam.TradeApi.Grpc.V1;
using Finam.TradeApi.Proto.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using System.Collections.Concurrent;
using static Finam.TradeApi.Grpc.V1.Candles;
using static Finam.TradeApi.Grpc.V1.Events;
using static Finam.TradeApi.Grpc.V1.Orders;
using static Finam.TradeApi.Grpc.V1.Portfolios;
using static Finam.TradeApi.Grpc.V1.Securities;
using static Finam.TradeApi.Grpc.V1.Stops;

namespace FinamClient
{
    /// <summary>
    /// Класс взаимодействия с Finam Trade Api по протоколу gRPC.
    /// Документация: https://finamweb.github.io/trade-api-docs/
    /// </summary>
    public class FinamApi : IDisposable
    {
        public event Action<Event>? EventResponse;
        public event Action<Exception>? Error;
        public event Action<string>? ConnectionStatusChanged;

        private readonly GrpcChannel _channel;
        private readonly SecuritiesClient _securitiesClient;
        private readonly PortfoliosClient _portfoliosClient;
        private readonly EventsClient _eventsClient;
        private readonly OrdersClient _ordersClient;
        private readonly StopsClient _stopsClient;
        private readonly CandlesClient _candlesClient;
        private readonly Metadata _metadata;
        private readonly string _url;
        private readonly string _token;
        private readonly object _lock = new();
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly SemaphoreSlim _reconnectSemaphore = new(1, 1);
        
        private AsyncDuplexStreamingCall<SubscriptionRequest, Event>? _eventsStream;
        private Task? _streamTask;
        private int _requestCounter;
        private bool _disposed;
        private int _reconnectAttempts;
        private readonly TimeSpan _maxReconnectDelay = TimeSpan.FromSeconds(60);
        private readonly ConcurrentDictionary<string, SubscriptionRequest> _activeSubscriptions = new();

        /// <summary>
        /// Создать класс FinamApi
        /// </summary>
        /// <param name="token">Токен авторизации</param>
        /// <param name="url">Точка входа (url)</param>
        public FinamApi(string token, string url = "https://trade-api.finam.ru")
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("Token cannot be null or empty", nameof(token));
            
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("URL cannot be null or empty", nameof(url));

            _token = token;
            _url = url;
            _metadata = new Metadata
            {
                { "X-Api-Key", token }
            };

            _channel = GrpcChannel.ForAddress(url, new GrpcChannelOptions
            {
                MaxReceiveMessageSize = 100 * 1024 * 1024,
                MaxSendMessageSize = 100 * 1024 * 1024,
                ThrowOperationCanceledOnCancellation = true
            });
            
            _securitiesClient = new SecuritiesClient(_channel);
            _portfoliosClient = new PortfoliosClient(_channel);
            _eventsClient = new EventsClient(_channel);
            _ordersClient = new OrdersClient(_channel);
            _stopsClient = new StopsClient(_channel);
            _candlesClient = new CandlesClient(_channel);

            StartEventStream();
        }

        /// <summary>
        /// Получение списка инструментов
        /// </summary>
        public async Task<GetSecuritiesResult> GetSecuritiesAsync(string? board = null, string? secCode = null, 
            CancellationToken cancellationToken = default)
        {
            try
            {
                var request = new GetSecuritiesRequest();
                if (!string.IsNullOrEmpty(board))
                    request.Board = board;
                if (!string.IsNullOrEmpty(secCode))
                    request.Seccode = secCode;

                var call = _securitiesClient.GetSecuritiesAsync(request, _metadata, cancellationToken: cancellationToken);
                var res = await call.ResponseAsync.ConfigureAwait(false);
                return res;
            }
            catch (RpcException ex)
            {
                HandleRpcException(ex, nameof(GetSecuritiesAsync));
                throw;
            }
        }

        /// <summary>
        /// Получение портфеля
        /// </summary>
        public async Task<GetPortfolioResult> GetPortfolioAsync(string clientId, bool includeCurrencies = true, 
            bool includeMaxBuySell = true, bool includeMoney = true, bool includePositions = true,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("ClientId cannot be null or empty", nameof(clientId));

            try
            {
                var res = await _portfoliosClient.GetPortfolioAsync(new GetPortfolioRequest
                {
                    ClientId = clientId,
                    Content = new PortfolioContent
                    {
                        IncludeCurrencies = includeCurrencies,
                        IncludeMaxBuySell = includeMaxBuySell,
                        IncludeMoney = includeMoney,
                        IncludePositions = includePositions,
                    }
                }, _metadata, cancellationToken: cancellationToken).ConfigureAwait(false);
                return res;
            }
            catch (RpcException ex)
            {
                HandleRpcException(ex, nameof(GetPortfolioAsync));
                throw;
            }
        }

        /// <summary>
        /// Получение заявок
        /// </summary>
        public async Task<GetOrdersResult> GetOrdersAsync(string clientId, bool includeActive = true,
            bool includeCanceled = true, bool includeMatched = true, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("ClientId cannot be null or empty", nameof(clientId));

            try
            {
                var res = await _ordersClient.GetOrdersAsync(new GetOrdersRequest
                {
                    ClientId = clientId,
                    IncludeActive = includeActive,
                    IncludeCanceled = includeCanceled,
                    IncludeMatched = includeMatched,
                }, _metadata, cancellationToken: cancellationToken).ConfigureAwait(false);
                return res;
            }
            catch (RpcException ex)
            {
                HandleRpcException(ex, nameof(GetOrdersAsync));
                throw;
            }
        }

        /// <summary>
        /// Выставление новой заявки
        /// </summary>
        public async Task<NewOrderResult> NewOrderAsync(string clientId, string secBoard, string secCode, 
            bool isBuy, int quantity, double? price = null, bool useCredit = false, 
            OrderProperty property = OrderProperty.PutInQueue, OrderCondition? condition = null, 
            OrderValidBefore? validBefore = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("ClientId cannot be null or empty", nameof(clientId));
            if (string.IsNullOrWhiteSpace(secBoard))
                throw new ArgumentException("SecurityBoard cannot be null or empty", nameof(secBoard));
            if (string.IsNullOrWhiteSpace(secCode))
                throw new ArgumentException("SecurityCode cannot be null or empty", nameof(secCode));
            if (quantity <= 0)
                throw new ArgumentException("Quantity must be positive", nameof(quantity));

            try
            {
                var request = new NewOrderRequest
                {
                    ClientId = clientId,
                    SecurityBoard = secBoard,
                    SecurityCode = secCode,
                    BuySell = isBuy ? BuySell.Buy : BuySell.Sell,
                    Quantity = quantity,
                    UseCredit = useCredit,
                    Property = property
                };

                if (price.HasValue)
                    request.Price = price.Value;

                if (condition != null)
                    request.Condition = condition;

                if (validBefore != null)
                    request.ValidBefore = validBefore;

                var res = await _ordersClient.NewOrderAsync(request, _metadata, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return res;
            }
            catch (RpcException ex)
            {
                HandleRpcException(ex, nameof(NewOrderAsync));
                throw;
            }
        }

        /// <summary>
        /// Отмена заявки
        /// </summary>
        public async Task<CancelOrderResult> CancelOrderAsync(string clientId, int transactionId, 
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("ClientId cannot be null or empty", nameof(clientId));

            try
            {
                var res = await _ordersClient.CancelOrderAsync(new CancelOrderRequest
                {
                    ClientId = clientId,
                    TransactionId = transactionId
                }, _metadata, cancellationToken: cancellationToken).ConfigureAwait(false);
                return res;
            }
            catch (RpcException ex)
            {
                HandleRpcException(ex, nameof(CancelOrderAsync));
                throw;
            }
        }

        /// <summary>
        /// Получение стоп-заявок
        /// </summary>
        public async Task<GetStopsResult> GetStopsAsync(string clientId, bool includeActive = true,
            bool includeCanceled = true, bool includeExecuted = true, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("ClientId cannot be null or empty", nameof(clientId));

            try
            {
                var res = await _stopsClient.GetStopsAsync(new GetStopsRequest
                {
                    ClientId = clientId,
                    IncludeActive = includeActive,
                    IncludeCanceled = includeCanceled,
                    IncludeExecuted = includeExecuted,
                }, _metadata, cancellationToken: cancellationToken).ConfigureAwait(false);
                return res;
            }
            catch (RpcException ex)
            {
                HandleRpcException(ex, nameof(GetStopsAsync));
                throw;
            }
        }

        /// <summary>
        /// Выставление новой стоп-заявки
        /// </summary>
        public async Task<NewStopResult> NewStopAsync(string clientId, string secBoard, string secCode,
            bool isBuy, StopLoss? stopLoss = null, TakeProfit? takeProfit = null,
            Timestamp? expirationDate = null, long? linkOrder = null, OrderValidBefore? validBefore = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("ClientId cannot be null or empty", nameof(clientId));
            if (string.IsNullOrWhiteSpace(secBoard))
                throw new ArgumentException("SecurityBoard cannot be null or empty", nameof(secBoard));
            if (string.IsNullOrWhiteSpace(secCode))
                throw new ArgumentException("SecurityCode cannot be null or empty", nameof(secCode));

            try
            {
                var request = new NewStopRequest
                {
                    ClientId = clientId,
                    SecurityBoard = secBoard,
                    SecurityCode = secCode,
                    BuySell = isBuy ? BuySell.Buy : BuySell.Sell
                };

                if (stopLoss != null)
                    request.StopLoss = stopLoss;

                if (takeProfit != null)
                    request.TakeProfit = takeProfit;

                if (expirationDate != null)
                    request.ExpirationDate = expirationDate;

                if (linkOrder.HasValue)
                    request.LinkOrder = linkOrder.Value;

                if (validBefore != null)
                    request.ValidBefore = validBefore;

                var res = await _stopsClient.NewStopAsync(request, _metadata, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return res;
            }
            catch (RpcException ex)
            {
                HandleRpcException(ex, nameof(NewStopAsync));
                throw;
            }
        }

        /// <summary>
        /// Отмена стоп-заявки
        /// </summary>
        public async Task<CancelStopResult> CancelStopAsync(string clientId, int stopId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("ClientId cannot be null or empty", nameof(clientId));

            try
            {
                var res = await _stopsClient.CancelStopAsync(new CancelStopRequest
                {
                    ClientId = clientId,
                    StopId = stopId
                }, _metadata, cancellationToken: cancellationToken).ConfigureAwait(false);
                return res;
            }
            catch (RpcException ex)
            {
                HandleRpcException(ex, nameof(CancelStopAsync));
                throw;
            }
        }

        /// <summary>
        /// Подписка на биржевой стакан
        /// </summary>
        public async Task SubscribeOrderBookAsync(string secBoard, string secCode, string? requestId = null)
        {
            if (string.IsNullOrWhiteSpace(secBoard))
                throw new ArgumentException("SecurityBoard cannot be null or empty", nameof(secBoard));
            if (string.IsNullOrWhiteSpace(secCode))
                throw new ArgumentException("SecurityCode cannot be null or empty", nameof(secCode));

            var id = requestId ?? GetRandomId();
            var subscriptionRequest = new SubscriptionRequest
            {
                OrderBookSubscribeRequest = new OrderBookSubscribeRequest
                {
                    RequestId = id,
                    SecurityBoard = secBoard,
                    SecurityCode = secCode
                }
            };

            var key = $"orderbook:{secBoard}:{secCode}";
            _activeSubscriptions[key] = subscriptionRequest;

            await WriteToStreamAsync(subscriptionRequest).ConfigureAwait(false);
        }

        /// <summary>
        /// Удаление подписки на биржевой стакан
        /// </summary>
        public async Task UnsubscribeOrderBookAsync(string secBoard, string secCode, string? requestId = null)
        {
            if (string.IsNullOrWhiteSpace(secBoard))
                throw new ArgumentException("SecurityBoard cannot be null or empty", nameof(secBoard));
            if (string.IsNullOrWhiteSpace(secCode))
                throw new ArgumentException("SecurityCode cannot be null or empty", nameof(secCode));

            var key = $"orderbook:{secBoard}:{secCode}";
            _activeSubscriptions.TryRemove(key, out _);

            await WriteToStreamAsync(new SubscriptionRequest
            {
                OrderBookUnsubscribeRequest = new OrderBookUnsubscribeRequest
                {
                    RequestId = requestId ?? GetRandomId(),
                    SecurityBoard = secBoard,
                    SecurityCode = secCode
                }
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Подписка на заявки и сделки
        /// </summary>
        public async Task SubscribeOrderTradeAsync(IEnumerable<string> clientIds, bool includeOrders = true,
            bool includeTrades = true, string? requestId = null)
        {
            if (clientIds == null || !clientIds.Any())
                throw new ArgumentException("ClientIds cannot be null or empty", nameof(clientIds));

            var id = requestId ?? GetRandomId();
            var subscriptionRequest = new SubscriptionRequest
            {
                OrderTradeSubscribeRequest = new OrderTradeSubscribeRequest
                {
                    RequestId = id,
                    ClientIds = { clientIds },
                    IncludeOrders = includeOrders,
                    IncludeTrades = includeTrades,
                }
            };

            var key = $"ordertrade:{string.Join(",", clientIds)}";
            _activeSubscriptions[key] = subscriptionRequest;

            await WriteToStreamAsync(subscriptionRequest).ConfigureAwait(false);
        }

        /// <summary>
        /// Удаление подписки на заявки и сделки
        /// </summary>
        public async Task UnsubscribeOrderTradeAsync(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId))
                throw new ArgumentException("RequestId cannot be null or empty", nameof(requestId));

            await WriteToStreamAsync(new SubscriptionRequest
            {
                OrderTradeUnsubscribeRequest = new OrderTradeUnsubscribeRequest
                {
                    RequestId = requestId
                }
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Отправка keep-alive запроса для поддержания активного соединения
        /// </summary>
        public async Task SendKeepAliveAsync(string? requestId = null)
        {
            await WriteToStreamAsync(new SubscriptionRequest
            {
                KeepAliveRequest = new KeepAliveRequest
                {
                    RequestId = requestId ?? GetRandomId()
                }
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Получение дневных свечей
        /// </summary>
        public async Task<GetDayCandlesResult> GetDayCandlesAsync(string secBoard, string secCode,
            DayCandleTimeFrame timeFrame, DayCandleInterval interval, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(secBoard))
                throw new ArgumentException("SecurityBoard cannot be null or empty", nameof(secBoard));
            if (string.IsNullOrWhiteSpace(secCode))
                throw new ArgumentException("SecurityCode cannot be null or empty", nameof(secCode));

            try
            {
                var res = await _candlesClient.GetDayCandlesAsync(new GetDayCandlesRequest
                {
                    SecurityBoard = secBoard,
                    SecurityCode = secCode,
                    TimeFrame = timeFrame,
                    Interval = interval
                }, _metadata, cancellationToken: cancellationToken).ConfigureAwait(false);
                return res;
            }
            catch (RpcException ex)
            {
                HandleRpcException(ex, nameof(GetDayCandlesAsync));
                throw;
            }
        }

        /// <summary>
        /// Получение внутридневных свечей
        /// </summary>
        public async Task<GetIntradayCandlesResult> GetIntradayCandlesAsync(string secBoard, string secCode,
            IntradayCandleTimeFrame timeFrame, IntradayCandleInterval interval, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(secBoard))
                throw new ArgumentException("SecurityBoard cannot be null or empty", nameof(secBoard));
            if (string.IsNullOrWhiteSpace(secCode))
                throw new ArgumentException("SecurityCode cannot be null or empty", nameof(secCode));

            try
            {
                var res = await _candlesClient.GetIntradayCandlesAsync(new GetIntradayCandlesRequest
                {
                    SecurityBoard = secBoard,
                    SecurityCode = secCode,
                    TimeFrame = timeFrame,
                    Interval = interval
                }, _metadata, cancellationToken: cancellationToken).ConfigureAwait(false);
                return res;
            }
            catch (RpcException ex)
            {
                HandleRpcException(ex, nameof(GetIntradayCandlesAsync));
                throw;
            }
        }

        /// <summary>
        /// Получить следующий уникальный id для запроса
        /// </summary>
        public string GetRandomId()
        {
            lock (_lock)
            {
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var counter = (_requestCounter++ % 10000).ToString().PadLeft(4, '0');
                return $"{timestamp}_{counter}";
            }
        }

        private void StartEventStream()
        {
            if (_disposed) return;

            try
            {
                _eventsStream?.Dispose();
                _eventsStream = _eventsClient.GetEvents(_metadata);
                _reconnectAttempts = 0;
                ConnectionStatusChanged?.Invoke("Connected");

                _streamTask = Task.Factory.StartNew(async () =>
                {
                    try
                    {
                        await foreach (var response in _eventsStream.ResponseStream.ReadAllAsync(_cancellationTokenSource.Token)
                            .ConfigureAwait(false))
                        {
                            try
                            {
                                EventResponse?.Invoke(response);
                            }
                            catch (Exception ex)
                            {
                                Error?.Invoke(new Exception($"Error in event handler: {ex.Message}", ex));
                            }
                        }
                    }
                    catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
                    {
                        // Expected on cancellation
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected on cancellation
                    }
                    catch (Exception ex)
                    {
                        if (!_disposed && !_cancellationTokenSource.Token.IsCancellationRequested)
                        {
                            Error?.Invoke(new Exception($"Event stream error: {ex.Message}", ex));
                            ConnectionStatusChanged?.Invoke("Disconnected");
                            await ReconnectStreamAsync().ConfigureAwait(false);
                        }
                    }
                }, TaskCreationOptions.LongRunning).Unwrap();
            }
            catch (Exception ex)
            {
                Error?.Invoke(new Exception($"Failed to start event stream: {ex.Message}", ex));
                ConnectionStatusChanged?.Invoke("Failed");
            }
        }

        private async Task ReconnectStreamAsync()
        {
            if (_disposed || _cancellationTokenSource.Token.IsCancellationRequested)
                return;

            await _reconnectSemaphore.WaitAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
            try
            {
                _reconnectAttempts++;
                var delay = CalculateBackoffDelay(_reconnectAttempts);
                
                ConnectionStatusChanged?.Invoke($"Reconnecting (attempt {_reconnectAttempts})...");
                await Task.Delay(delay, _cancellationTokenSource.Token).ConfigureAwait(false);

                StartEventStream();

                await Task.Delay(TimeSpan.FromSeconds(1), _cancellationTokenSource.Token).ConfigureAwait(false);
                await ResubscribeAllAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }
            catch (Exception ex)
            {
                Error?.Invoke(new Exception($"Reconnection failed: {ex.Message}", ex));
                if (_reconnectAttempts < 10)
                {
                    await ReconnectStreamAsync().ConfigureAwait(false);
                }
                else
                {
                    ConnectionStatusChanged?.Invoke("Failed - max reconnect attempts reached");
                }
            }
            finally
            {
                _reconnectSemaphore.Release();
            }
        }

        private TimeSpan CalculateBackoffDelay(int attemptNumber)
        {
            var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attemptNumber - 1), _maxReconnectDelay.TotalSeconds));
            return delay;
        }

        private async Task ResubscribeAllAsync()
        {
            foreach (var subscription in _activeSubscriptions.Values)
            {
                try
                {
                    await WriteToStreamAsync(subscription).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Error?.Invoke(new Exception($"Failed to resubscribe: {ex.Message}", ex));
                }
            }
        }

        private async Task WriteToStreamAsync(SubscriptionRequest request)
        {
            if (_disposed || _eventsStream == null)
                throw new ObjectDisposedException(nameof(FinamApi));

            try
            {
                await _eventsStream.RequestStream.WriteAsync(request).ConfigureAwait(false);
            }
            catch (RpcException ex)
            {
                HandleRpcException(ex, "WriteToStream");
                throw;
            }
        }

        private void HandleRpcException(RpcException ex, string methodName)
        {
            var errorMessage = ex.StatusCode switch
            {
                StatusCode.Unauthenticated => "Authentication failed. Check your API token.",
                StatusCode.PermissionDenied => "Permission denied. Check your account permissions.",
                StatusCode.ResourceExhausted => "Rate limit exceeded. Please wait before retrying.",
                StatusCode.Unavailable => "Service temporarily unavailable. Will retry automatically.",
                StatusCode.DeadlineExceeded => "Request timeout. The server took too long to respond.",
                StatusCode.InvalidArgument => $"Invalid argument: {ex.Status.Detail}",
                StatusCode.NotFound => "Resource not found.",
                StatusCode.AlreadyExists => "Resource already exists.",
                StatusCode.FailedPrecondition => $"Failed precondition: {ex.Status.Detail}",
                StatusCode.Cancelled => "Operation was cancelled.",
                _ => $"RPC error: {ex.Status.Detail}"
            };

            Error?.Invoke(new Exception($"{methodName}: {errorMessage}", ex));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _cancellationTokenSource.Cancel();
                _eventsStream?.Dispose();
                _streamTask?.Wait(TimeSpan.FromSeconds(5));
                _channel?.Dispose();
                _cancellationTokenSource?.Dispose();
                _reconnectSemaphore?.Dispose();
            }
            catch (Exception ex)
            {
                Error?.Invoke(new Exception($"Error during disposal: {ex.Message}", ex));
            }
        }
    }
}
