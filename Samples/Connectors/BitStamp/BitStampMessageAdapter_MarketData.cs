namespace StockSharp.BitStamp;

partial class BitStampMessageAdapter
{
	private const string _eurusd = "eurusd";

	private void SessionOnNewTrade(string pair, Trade trade)
	{
		SendOutMessage(new ExecutionMessage
		{
			DataTypeEx = DataType.Ticks,
			SecurityId = pair.ToStockSharp(),
			TradeId = trade.Id,
			TradePrice = (decimal)trade.Price,
			TradeVolume = (decimal)trade.Amount,
			ServerTime = trade.Time,
			OriginSide = trade.Type.ToSide(),
		});
	}

	private void SessionOnNewOrderBook(string pair, OrderBook book)
	{
		SendOutMessage(new QuoteChangeMessage
		{
			SecurityId = pair.ToStockSharp(),
			Bids = book.Bids.Select(e => new QuoteChange(e.Price, e.Size)).ToArray(),
			Asks = book.Asks.Select(e => new QuoteChange(e.Price, e.Size)).ToArray(),
			ServerTime = book.Time,
		});
	}

	private void SessionOnNewOrderLog(string pair, OrderStates state, Order order)
	{
		SendOutMessage(new ExecutionMessage
		{
			DataTypeEx = DataType.OrderLog,
			SecurityId = pair.ToStockSharp(),
			ServerTime = order.Time,
			OrderVolume = (decimal)order.Amount,
			OrderPrice = (decimal)order.Price,
			OrderId = order.Id,
			Side = order.Type.ToSide(),
			OrderState = state,
		});
	}

	/// <inheritdoc />
	protected override async ValueTask OnTFCandlesSubscriptionAsync(MarketDataMessage mdMsg, CancellationToken cancellationToken)
	{
		SendSubscriptionReply(mdMsg.TransactionId);

		var currency = mdMsg.SecurityId.ToCurrency();

		if (mdMsg.IsSubscribe)
		{
			var step = (int)mdMsg.GetTimeFrame().TotalSeconds;

			if (mdMsg.From is not null || mdMsg.To is not null)
			{
				var from = mdMsg.From?.UtcDateTime ?? DateTime.Today;
				var to = mdMsg.To?.UtcDateTime ?? DateTime.UtcNow;
				var left = mdMsg.Count ?? long.MaxValue;

				while (true)
				{
					var ohlc = await _httpClient.GetOhlc(currency, step, 1000, from, cancellationToken);

					var hasData = false;

					foreach (var c in ohlc.OrderBy(t => t.Time))
					{
						if (c.Time <= from)
							continue;

						if (c.Time > to)
							return;

						SendOutMessage(new TimeFrameCandleMessage
						{
							OriginalTransactionId = mdMsg.TransactionId,

							OpenTime = c.Time,

							OpenPrice = (decimal)c.Open,
							HighPrice = (decimal)c.High,
							LowPrice = (decimal)c.Low,
							ClosePrice = (decimal)c.Close,
							TotalVolume = (decimal)c.Volume,

							State = CandleStates.Finished,
						});

						if (--left <= 0)
							return;

						hasData = true;

						from = c.Time;
					}

					if (!hasData)
						break;

					await IterationInterval.Delay(cancellationToken);
				}
			}

			if (mdMsg.IsHistoryOnly())
				return;

			// bitstamp does not support web sockets for candles
		}
		else
		{
			// bitstamp does not support web sockets for candles
		}
	}

	/// <inheritdoc />
	protected override ValueTask OnMarketDepthSubscriptionAsync(MarketDataMessage mdMsg, CancellationToken cancellationToken)
	{
		SendSubscriptionReply(mdMsg.TransactionId);

		var currency = mdMsg.SecurityId.ToCurrency();

		if (mdMsg.IsSubscribe)
			return _pusherClient.SubscribeOrderBook(currency, cancellationToken);
		else
			return _pusherClient.UnSubscribeOrderBook(currency, cancellationToken);
	}

	/// <inheritdoc />
	protected override ValueTask OnOrderLogSubscriptionAsync(MarketDataMessage mdMsg, CancellationToken cancellationToken)
	{
		SendSubscriptionReply(mdMsg.TransactionId);

		var currency = mdMsg.SecurityId.ToCurrency();

		if (mdMsg.IsSubscribe)
			return _pusherClient.SubscribeOrderLog(currency, cancellationToken);
		else
			return _pusherClient.UnSubscribeOrderLog(currency, cancellationToken);
	}

	/// <inheritdoc />
	protected override async ValueTask OnTicksSubscriptionAsync(MarketDataMessage mdMsg, CancellationToken cancellationToken)
	{
		SendSubscriptionReply(mdMsg.TransactionId);

		var currency = mdMsg.SecurityId.ToCurrency();

		if (mdMsg.IsSubscribe)
		{
			if (mdMsg.From is not null || mdMsg.To is not null)
			{
				var from = mdMsg.From?.UtcDateTime ?? DateTime.Today;
				var to = mdMsg.To?.UtcDateTime ?? DateTime.UtcNow;

				var trades = await _httpClient.GetTransactions(currency, "day", cancellationToken);

				foreach (var trade in trades.OrderBy(t => t.Time))
				{
					if (trade.Time < from)
						continue;

					if (trade.Time > to)
						break;

					SendOutMessage(new ExecutionMessage
					{
						DataTypeEx = DataType.Ticks,
						SecurityId = mdMsg.SecurityId,
						TradeId = trade.Id,
						TradePrice = (decimal)trade.Price,
						TradeVolume = trade.Amount.ToDecimal(),
						ServerTime = trade.Time,
						OriginSide = trade.Type.ToSide(),
						OriginalTransactionId = mdMsg.TransactionId
					});
				}
			}

			if (mdMsg.IsHistoryOnly())
				return;
			
			await _pusherClient.SubscribeTrades(currency, cancellationToken);
		}
		else
		{
			await _pusherClient.UnSubscribeTrades(currency, cancellationToken);
		}
	}

	/// <inheritdoc />
	public override async ValueTask SecurityLookupAsync(SecurityLookupMessage lookupMsg, CancellationToken cancellationToken)
	{
		var secTypes = lookupMsg.GetSecurityTypes();
		var left = lookupMsg.Count ?? long.MaxValue;

		foreach (var info in await _httpClient.GetPairsInfo(cancellationToken))
		{
			var secMsg = new SecurityMessage
			{
				SecurityId = info.Name.ToStockSharp(),
				SecurityType = info.UrlSymbol == _eurusd ? SecurityTypes.Currency : SecurityTypes.CryptoCurrency,
				MinVolume = info.MinimumOrder[..info.MinimumOrder.IndexOf(' ')].To<decimal>(),
				Decimals = info.BaseDecimals,
				Name = info.Description,
				VolumeStep = info.UrlSymbol == _eurusd ? 0.00001m : 0.00000001m,
				OriginalTransactionId = lookupMsg.TransactionId,
			};

			if (!secMsg.IsMatch(lookupMsg, secTypes))
				continue;

			SendOutMessage(secMsg);

			if (--left <= 0)
				break;
		}
	}
}