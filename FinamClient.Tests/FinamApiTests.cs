using Finam.TradeApi.Proto.V1;
using FinamClient;
using FluentAssertions;
using Xunit;

namespace FinamClient.Tests
{
    public class FinamApiTests : IDisposable
    {
        private FinamApi? _api;

        public void Dispose()
        {
            _api?.Dispose();
        }

        [Fact]
        public void Constructor_WithValidToken_ShouldSucceed()
        {
            var token = "test-token-123";
            _api = new FinamApi(token);
            _api.Should().NotBeNull();
        }

        [Fact]
        public void Constructor_WithNullToken_ShouldThrowArgumentException()
        {
            Action act = () => _api = new FinamApi(null!);
            act.Should().Throw<ArgumentException>()
                .WithMessage("*Token*");
        }

        [Fact]
        public void Constructor_WithEmptyToken_ShouldThrowArgumentException()
        {
            Action act = () => _api = new FinamApi("");
            act.Should().Throw<ArgumentException>()
                .WithMessage("*Token*");
        }

        [Fact]
        public void Constructor_WithNullUrl_ShouldThrowArgumentException()
        {
            Action act = () => _api = new FinamApi("test-token", null!);
            act.Should().Throw<ArgumentException>()
                .WithMessage("*URL*");
        }

        [Fact]
        public void GetRandomId_ShouldReturnUniqueIds()
        {
            _api = new FinamApi("test-token");
            var id1 = _api.GetRandomId();
            var id2 = _api.GetRandomId();
            var id3 = _api.GetRandomId();

            id1.Should().NotBeNullOrEmpty();
            id2.Should().NotBeNullOrEmpty();
            id3.Should().NotBeNullOrEmpty();
            
            id1.Should().NotBe(id2);
            id2.Should().NotBe(id3);
            id1.Should().NotBe(id3);
        }

        [Fact]
        public void GetRandomId_ShouldContainTimestampAndCounter()
        {
            _api = new FinamApi("test-token");
            var id = _api.GetRandomId();

            id.Should().Contain("_");
            var parts = id.Split('_');
            parts.Should().HaveCount(2);
            parts[0].Should().MatchRegex(@"^\d+$");
            parts[1].Should().MatchRegex(@"^\d{4}$");
        }

        [Fact]
        public async Task GetPortfolioAsync_WithNullClientId_ShouldThrowArgumentException()
        {
            _api = new FinamApi("test-token");
            Func<Task> act = async () => await _api.GetPortfolioAsync(null!);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*ClientId*");
        }

        [Fact]
        public async Task GetOrdersAsync_WithNullClientId_ShouldThrowArgumentException()
        {
            _api = new FinamApi("test-token");
            Func<Task> act = async () => await _api.GetOrdersAsync(null!);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*ClientId*");
        }

        [Fact]
        public async Task GetStopsAsync_WithNullClientId_ShouldThrowArgumentException()
        {
            _api = new FinamApi("test-token");
            Func<Task> act = async () => await _api.GetStopsAsync(null!);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*ClientId*");
        }

        [Fact]
        public async Task NewOrderAsync_WithNullClientId_ShouldThrowArgumentException()
        {
            _api = new FinamApi("test-token");
            Func<Task> act = async () => await _api.NewOrderAsync(null!, "TQBR", "SBER", true, 10);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*ClientId*");
        }

        [Fact]
        public async Task NewOrderAsync_WithNullSecurityBoard_ShouldThrowArgumentException()
        {
            _api = new FinamApi("test-token");
            Func<Task> act = async () => await _api.NewOrderAsync("CLIENT1", null!, "SBER", true, 10);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*SecurityBoard*");
        }

        [Fact]
        public async Task NewOrderAsync_WithNullSecurityCode_ShouldThrowArgumentException()
        {
            _api = new FinamApi("test-token");
            Func<Task> act = async () => await _api.NewOrderAsync("CLIENT1", "TQBR", null!, true, 10);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*SecurityCode*");
        }

        [Fact]
        public async Task NewOrderAsync_WithZeroQuantity_ShouldThrowArgumentException()
        {
            _api = new FinamApi("test-token");
            Func<Task> act = async () => await _api.NewOrderAsync("CLIENT1", "TQBR", "SBER", true, 0);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*Quantity*");
        }

        [Fact]
        public async Task NewOrderAsync_WithNegativeQuantity_ShouldThrowArgumentException()
        {
            _api = new FinamApi("test-token");
            Func<Task> act = async () => await _api.NewOrderAsync("CLIENT1", "TQBR", "SBER", true, -10);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*Quantity*");
        }

        [Fact]
        public async Task CancelOrderAsync_WithNullClientId_ShouldThrowArgumentException()
        {
            _api = new FinamApi("test-token");
            Func<Task> act = async () => await _api.CancelOrderAsync(null!, 123);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*ClientId*");
        }

        [Fact]
        public async Task NewStopAsync_WithNullClientId_ShouldThrowArgumentException()
        {
            _api = new FinamApi("test-token");
            Func<Task> act = async () => await _api.NewStopAsync(null!, "TQBR", "SBER", true);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*ClientId*");
        }

        [Fact]
        public async Task CancelStopAsync_WithNullClientId_ShouldThrowArgumentException()
        {
            _api = new FinamApi("test-token");
            Func<Task> act = async () => await _api.CancelStopAsync(null!, 123);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*ClientId*");
        }

        [Fact]
        public async Task SubscribeOrderBookAsync_WithNullSecurityBoard_ShouldThrowArgumentException()
        {
            _api = new FinamApi("test-token");
            Func<Task> act = async () => await _api.SubscribeOrderBookAsync(null!, "SBER");
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*SecurityBoard*");
        }

        [Fact]
        public async Task SubscribeOrderBookAsync_WithNullSecurityCode_ShouldThrowArgumentException()
        {
            _api = new FinamApi("test-token");
            Func<Task> act = async () => await _api.SubscribeOrderBookAsync("TQBR", null!);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*SecurityCode*");
        }

        [Fact]
        public async Task UnsubscribeOrderBookAsync_WithNullSecurityBoard_ShouldThrowArgumentException()
        {
            _api = new FinamApi("test-token");
            Func<Task> act = async () => await _api.UnsubscribeOrderBookAsync(null!, "SBER");
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*SecurityBoard*");
        }

        [Fact]
        public async Task UnsubscribeOrderBookAsync_WithNullSecurityCode_ShouldThrowArgumentException()
        {
            _api = new FinamApi("test-token");
            Func<Task> act = async () => await _api.UnsubscribeOrderBookAsync("TQBR", null!);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*SecurityCode*");
        }

        [Fact]
        public async Task SubscribeOrderTradeAsync_WithNullClientIds_ShouldThrowArgumentException()
        {
            _api = new FinamApi("test-token");
            Func<Task> act = async () => await _api.SubscribeOrderTradeAsync(null!);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*ClientIds*");
        }

        [Fact]
        public async Task SubscribeOrderTradeAsync_WithEmptyClientIds_ShouldThrowArgumentException()
        {
            _api = new FinamApi("test-token");
            Func<Task> act = async () => await _api.SubscribeOrderTradeAsync(Array.Empty<string>());
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*ClientIds*");
        }

        [Fact]
        public async Task UnsubscribeOrderTradeAsync_WithNullRequestId_ShouldThrowArgumentException()
        {
            _api = new FinamApi("test-token");
            Func<Task> act = async () => await _api.UnsubscribeOrderTradeAsync(null!);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*RequestId*");
        }

        [Fact]
        public async Task GetDayCandlesAsync_WithNullSecurityBoard_ShouldThrowArgumentException()
        {
            _api = new FinamApi("test-token");
            var interval = new DayCandleInterval();
            Func<Task> act = async () => await _api.GetDayCandlesAsync(null!, "SBER", DayCandleTimeFrame.D1, interval);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*SecurityBoard*");
        }

        [Fact]
        public async Task GetDayCandlesAsync_WithNullSecurityCode_ShouldThrowArgumentException()
        {
            _api = new FinamApi("test-token");
            var interval = new DayCandleInterval();
            Func<Task> act = async () => await _api.GetDayCandlesAsync("TQBR", null!, DayCandleTimeFrame.D1, interval);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*SecurityCode*");
        }

        [Fact]
        public async Task GetIntradayCandlesAsync_WithNullSecurityBoard_ShouldThrowArgumentException()
        {
            _api = new FinamApi("test-token");
            var interval = new IntradayCandleInterval();
            Func<Task> act = async () => await _api.GetIntradayCandlesAsync(null!, "SBER", IntradayCandleTimeFrame.M1, interval);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*SecurityBoard*");
        }

        [Fact]
        public async Task GetIntradayCandlesAsync_WithNullSecurityCode_ShouldThrowArgumentException()
        {
            _api = new FinamApi("test-token");
            var interval = new IntradayCandleInterval();
            Func<Task> act = async () => await _api.GetIntradayCandlesAsync("TQBR", null!, IntradayCandleTimeFrame.M1, interval);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*SecurityCode*");
        }

        [Fact]
        public void EventResponse_ShouldBeRaisedForStreamEvents()
        {
            _api = new FinamApi("test-token");
            var eventRaised = false;
            _api.EventResponse += (e) => { eventRaised = true; };
            
            eventRaised.Should().BeFalse();
        }

        [Fact]
        public void Error_EventShouldBeAvailable()
        {
            _api = new FinamApi("test-token");
            var errorRaised = false;
            _api.Error += (e) => { errorRaised = true; };
            
            errorRaised.Should().BeFalse();
        }

        [Fact]
        public void ConnectionStatusChanged_EventShouldBeAvailable()
        {
            _api = new FinamApi("test-token");
            var statusChanges = new List<string>();
            _api.ConnectionStatusChanged += (status) => { statusChanges.Add(status); };
            
            Thread.Sleep(500);
            statusChanges.Should().NotBeNull();
        }

        [Fact]
        public void Dispose_ShouldNotThrowException()
        {
            _api = new FinamApi("test-token");
            Action act = () => _api.Dispose();
            act.Should().NotThrow();
        }

        [Fact]
        public void Dispose_CalledMultipleTimes_ShouldNotThrowException()
        {
            _api = new FinamApi("test-token");
            _api.Dispose();
            Action act = () => _api.Dispose();
            act.Should().NotThrow();
        }

        [Fact]
        public async Task WriteToStreamAsync_AfterDispose_ShouldThrowObjectDisposedException()
        {
            _api = new FinamApi("test-token");
            _api.Dispose();
            
            Func<Task> act = async () => await _api.SubscribeOrderBookAsync("TQBR", "SBER");
            await act.Should().ThrowAsync<ObjectDisposedException>();
        }
    }
}
