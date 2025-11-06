# Finam Trade API v1 - Breaking Changes Documentation

## Overview
This document captures the breaking changes introduced when upgrading from the legacy Finam Trade API proto definitions to the latest official v1 API from [finamweb/trade-api-docs](https://github.com/FinamWeb/trade-api-docs).

## Updated: November 6, 2025

---

## Package Version Updates

### NuGet Package Updates
| Package | Old Version | New Version |
|---------|-------------|-------------|
| Google.Protobuf | 3.22.3 | 3.31.1 |
| Grpc.Net.Client | 2.52.0 | 2.70.0 |
| Grpc.Tools | 2.54.0 | 2.68.1 |
| Google.Api.CommonProtos | (new) | 2.17.0 |

### Rationale
- Updated to latest stable gRPC packages for better performance and security
- Added Google.Api.CommonProtos for google.type.Date support (required by candles API)

---

## Proto File Changes

### 1. common.proto

#### New Message Type: Decimal
A new `Decimal` message type was added for precise decimal number representation:

```protobuf
message Decimal {
  int64 num = 1;      // Mantissa
  uint32 scale = 2;   // Exponent for base 10
}
```

**Migration Note:** This type is used in the new Candles API. The calculation is: `value = num * 10^-scale`

**Example:** The number "250.655" = Decimal{num: 250655, scale: 3}

---

### 2. events.proto

#### New Message Type: KeepAliveRequest
A new keep-alive request type was added for maintaining active connections:

```protobuf
message KeepAliveRequest {
  string request_id = 1;
}
```

#### Updated SubscriptionRequest
The `SubscriptionRequest` message now includes a new field:

```protobuf
message SubscriptionRequest {
  oneof payload {
    // ... existing fields ...
    proto.tradeapi.v1.KeepAliveRequest keep_alive_request = 5;  // NEW
  }
}
```

**Migration Impact:**
- Applications should periodically send keep-alive requests to maintain streaming connections
- Recommended to send keep-alive every 30-60 seconds during idle periods

**Code Example:**
```csharp
await finamApi.SendKeepAliveAsync();
```

---

### 3. security.proto

#### Updated Field: instrument_code (DEPRECATED)
The `instrument_code` field in the `Security` message has been marked as reserved (deprecated):

```protobuf
message Security {
  // OLD: string instrument_code = 8;
  reserved 8;
  reserved "instrument_code";
}
```

**Migration Impact:**
- Remove all references to `Security.InstrumentCode`
- Use `Security.Code` or `Security.Ticker` instead

#### Updated Documentation for PriceSign enum
Enhanced documentation was added to explain each price sign value:
- `PRICE_SIGN_UNSPECIFIED`: Used when price information is not set (new IPOs, server recovery)
- `PRICE_SIGN_POSITIVE`: Price is positive (stocks, bonds, funds)
- `PRICE_SIGN_NON_NEGATIVE`: Price can be zero or positive (cryptocurrencies, zero-coupon bonds)
- `PRICE_SIGN_ANY`: Any price value allowed (futures, options)

#### Updated Properties Documentation
The `properties` field documentation was updated to use decimal notation instead of hexadecimal:
- Old: 0x01, 0x02, 0x04, etc.
- New: 1, 2, 4, 8, 16, 32, 48, 64, 128

**Migration Impact:** No code changes required - this is documentation only.

---

### 4. stops.proto

#### Updated Documentation
The `STOP_PRICE_UNITS_PIPS` enum value documentation was clarified:
- Old: "Значение в лотах" (Value in lots)
- New: "Значение в единицах цены" (Value in price units)

**Migration Impact:** No code changes required - this is clarification only.

---

### 5. securities.proto (gRPC service)

#### Updated GetSecuritiesRequest
The `GetSecuritiesRequest` message now supports optional filtering:

```protobuf
message GetSecuritiesRequest {
  google.protobuf.StringValue board = 1;    // NEW - Filter by trading board
  google.protobuf.StringValue seccode = 2;  // NEW - Filter by security code
}
```

**Migration Impact:**
- Old calls with `new GetSecuritiesRequest()` continue to work (returns all securities)
- New optional parameters allow filtering specific instruments

**Code Example:**
```csharp
// Get all securities (backward compatible)
var allSecurities = await finamApi.GetSecuritiesAsync();

// Get securities for specific board (new feature)
var request = new GetSecuritiesRequest 
{ 
    Board = "TQBR" 
};
```

---

### 6. candles.proto (NEW)

A completely new Candles API was added for retrieving historical price data.

#### New Message Types:
- `DayCandle` - Daily candlestick data
- `IntradayCandle` - Intraday candlestick data
- `DayCandleInterval` - Date range for daily candles
- `IntradayCandleInterval` - Timestamp range for intraday candles
- `GetDayCandlesRequest` / `GetDayCandlesResult`
- `GetIntradayCandlesRequest` / `GetIntradayCandlesResult`

#### New Enums:
- `IntradayCandleTimeFrame` - M1, M5, M15, H1
- `DayCandleTimeFrame` - D1, W1

#### Candles gRPC Service:
```protobuf
service Candles {
  rpc GetDayCandles(GetDayCandlesRequest) returns (GetDayCandlesResult);
  rpc GetIntradayCandles(GetIntradayCandlesRequest) returns (GetIntradayCandlesResult);
}
```

**Migration Impact:**
- This is a new feature - no breaking changes for existing code
- All candle prices use the new `Decimal` type for precision

**Code Example:**
```csharp
// Get daily candles
var dayCandlesResult = await finamApi.GetDayCandlesAsync(
    secBoard: "TQBR",
    secCode: "SBER",
    timeFrame: DayCandleTimeFrame.D1,
    interval: new DayCandleInterval 
    { 
        From = new Google.Type.Date { Year = 2024, Month = 1, Day = 1 },
        To = new Google.Type.Date { Year = 2024, Month = 12, Day = 31 }
    }
);

// Get intraday candles
var intradayCandlesResult = await finamApi.GetIntradayCandlesAsync(
    secBoard: "TQBR",
    secCode: "SBER",
    timeFrame: IntradayCandleTimeFrame.M5,
    interval: new IntradayCandleInterval 
    { 
        From = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(-1)),
        To = Timestamp.FromDateTime(DateTime.UtcNow)
    }
);
```

---

## FinamApi.cs Changes

### New Public Methods

#### SendKeepAliveAsync
```csharp
public async Task SendKeepAliveAsync(string? requestId = null)
```
Send keep-alive requests to maintain active streaming connections.

#### GetDayCandlesAsync
```csharp
public async Task<GetDayCandlesResult> GetDayCandlesAsync(
    string secBoard, 
    string secCode,
    DayCandleTimeFrame timeFrame, 
    DayCandleInterval interval)
```
Retrieve daily candlestick data.

#### GetIntradayCandlesAsync
```csharp
public async Task<GetIntradayCandlesResult> GetIntradayCandlesAsync(
    string secBoard, 
    string secCode,
    IntradayCandleTimeFrame timeFrame, 
    IntradayCandleInterval interval)
```
Retrieve intraday candlestick data.

---

## Authentication & Headers

### No Changes
- Authentication continues to use `X-Api-Key` header
- No changes to token format or authentication mechanism

---

## Streaming Semantics

### Keep-Alive Support
The event streaming API now supports keep-alive messages to maintain long-lived connections:
- Clients should send periodic `KeepAliveRequest` messages
- Prevents connection timeouts during idle periods
- No response is generated for keep-alive requests

**Recommendation:** Send keep-alive every 30-60 seconds when no other requests are being sent.

---

## Migration Checklist

### Required Changes:
- [ ] Update NuGet package references in your .csproj file
- [ ] Remove any references to `Security.InstrumentCode` (use `Security.Code` or `Security.Ticker`)
- [ ] Test all existing API calls to ensure backward compatibility

### Optional Enhancements:
- [ ] Implement keep-alive logic for long-lived streaming connections
- [ ] Add support for the new Candles API if historical data is needed
- [ ] Update `GetSecuritiesAsync` calls to use new filtering options

### Testing:
- [ ] Verify order placement and cancellation still works
- [ ] Confirm portfolio and positions retrieval
- [ ] Test event streaming (order book, trades, orders)
- [ ] Validate securities list retrieval

---

## Compatibility Notes

### Backward Compatibility:
Most changes are **backward compatible** - existing code will continue to work without modifications.

### Forward Compatibility:
New features (Candles API, Keep-Alive, GetSecurities filtering) are additive and do not break existing functionality.

---

## Support & Documentation

- Official Documentation: https://finamweb.github.io/trade-api-docs/
- Proto Definitions: https://github.com/FinamWeb/trade-api-docs/tree/master/contracts
- Issue Tracker: https://github.com/FinamWeb/trade-api-docs/issues

---

## Summary

The updated Finam Trade API v1 brings:
1. **New Candles API** - Historical price data with precise decimal representation
2. **Keep-Alive Support** - Better connection stability for streaming
3. **Enhanced GetSecurities** - Optional filtering by board and security code
4. **Documentation Improvements** - Better clarity on enum values and field usage
5. **Package Updates** - Latest stable gRPC and protobuf packages

The only **breaking change** is the removal of the deprecated `Security.InstrumentCode` field, which should be replaced with `Security.Code` or `Security.Ticker`.
