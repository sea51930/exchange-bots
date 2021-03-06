﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using WebSocket4Net;

using Common;
using Common.WebsocketProxy;
using RippleBot.Business;
using RippleBot.Business.DataApi;


namespace RippleBot
{
    /* TODO: this class is ripe for refactoring as business logic leaks here... or maybe whole system is.
     *       I imagine something like state machine, i.e. every action (done by request) gets this into
     *       a state that decides next action.
     */
    internal class RippleApi
    {
        private const int SOCKET_TIMEOUT = 12000;
        private string _dataApiUrl;
        private const int DATA_TIMEOUT = 40 * 1000;     //40sec timeout for data API
        private const byte RETRY_COUNT = 10;
        private const int RETRY_DELAY = 2000;
        private const string MANUAL_ORDER_SIGN = "12345";
        private readonly string _issuerAddress;     //BitStamp, SnapSwap, RippleCN or so
        private readonly string _fiatCurreny;

        private readonly string _rippleSocketUri;
        private readonly string _walletAddress;

        private readonly Logger _logger;
        private bool _open;
        private bool _closedByUser;

        private readonly WebSocket _webSocket;
        private readonly WebProxy _webProxy;

        private string _lastResponse;
        private readonly Regex _offerPattern = new Regex("\"taker_(?<verb>get|pay)s\":\"(?<value>\\d{1,20})\"");


        internal RippleApi(Logger logger, string dataApiUrl, string exchIssuerAddress, string fiatCurrencyCode)
        {
            _logger = logger;
            _dataApiUrl = dataApiUrl;
            _issuerAddress = exchIssuerAddress;
            _fiatCurreny = fiatCurrencyCode;

            _webSocket = new WebSocket(_rippleSocketUri = Configuration.GetValue("server"));

            var proxyHost = Configuration.GetValue("proxyHost");
            var proxyPort = Configuration.GetValue("proxyPort");
            if (null != proxyHost && null != proxyPort)
            {
                //TODO: these two lines don't belong here, rather to WebClient2
                _webProxy = new WebProxy(proxyHost, int.Parse(proxyPort));
                _webProxy.Credentials = CredentialCache.DefaultCredentials;

                var wsProxy = new HttpConnectProxy(new DnsEndPoint(proxyHost, Int32.Parse(proxyPort)), "1.0");
                _webSocket.Proxy = wsProxy;
            }

            _webSocket.Opened += websocket_Opened;
            _webSocket.Error += websocket_Error;
            _webSocket.Closed += websocket_Closed;
            _webSocket.MessageReceived += websocket_MessageReceived;
            _walletAddress = Configuration.AccessKey;
        }

        internal void Init()
        {
            _webSocket.Open();

            while (!_open)
            {
                Thread.Sleep(250);
            }
        }

        /// <summary>Get underlying account's balance of given asset.</summary>
        /// <param name="assetCode">Asset code (USD, JPY, XRP...)</param>
        /// <param name="assetGateway">
        /// Gateway that issues the asset or <code>null</code> for native asset (XRP). If the account has
        /// just one trust line with that asset code, this parameter can also be null.
        /// </param>
        /// <remarks>Uses data API (REST-like)</remarks>
        internal double GetBalance2(string assetCode, string assetGateway=null)
        {
            var url = String.Format("{0}/accounts/{1}/balances", _dataApiUrl, _walletAddress);

            var webClient = new WebClient2(_logger, DATA_TIMEOUT);

            BalancesResponse balanceData = webClient.DownloadObject<BalancesResponse>(url);

            if (null == balanceData || balanceData.IsError || null == balanceData.balances)
            {
                return -1.0;
            }

            return balanceData.Asset(assetCode, assetGateway);
        }

        /// <summary>Get native asset balance for given account.</summary>
        /// <remarks>
        /// Uses websockets as we need most recent value (data API shows last closed ledger)
        /// </remarks>
        internal double GetXrpBalance()
        {
            var command = new AccountInfoRequest { account = _walletAddress };

            var data = sendToRippleNet(Helpers.SerializeJson(command));

            if (null == data)
            {
                return -1.0;
            }

            if (!checkError("GetXrpBalance", data))
            {
                return -1.0;
            }

            var account = Helpers.DeserializeJSON<AccountInfoResponse>(data);
            if (null == account.result || null == account.result.account_data)
            {
                return -1.0;
            }

            return account.result.account_data.BalanceXrp;
        }

        internal double GetBalance(string currencyCode, string issuerAddress=null)      //TODO: in case of problems with GetBalance2, delete that and use this
        {
            var command = new AccountLinesRequest { account = _walletAddress };

            var data = sendToRippleNet(Helpers.SerializeJson(command));

            if (null == data)
            {
                return -1.0;
            }

            if (!checkError("GetBalance(" + currencyCode + ")", data))
            {
                return -1.0;
            }

            var account = Helpers.DeserializeJSON<AccountLinesResponse>(data);
            if (null == account.result.lines)
            {
                return -1.0;
            }

            var theLine = account.result.lines.SingleOrDefault(line =>
                                                               (String.IsNullOrEmpty(issuerAddress) || line.account == issuerAddress) &&
                                                               line.currency == currencyCode);
            if (null == theLine)
            {
                return -1.0;
            }

            return theLine.Balance;
        }

        internal Offer GetOrderInfo(int orderId)        //TODO: delete
        {
            var offerList = getActiveOrders();
            if (null == offerList || null == offerList.result || null == offerList.result.offers)
            {
                return null;
            }
            var order = offerList.result.offers.FirstOrDefault(o => o.seq == orderId);

            //NULL means it was already filled or canceled
            if (null == order)
            {
                return new Offer(true);
            }
            return order;
        }

        internal Order GetOrderInfo2(int orderId)
        {
            AccountOrdersResponse orderList = getActiveOrders2();

            if (null == orderList)
            {
                return null;
            }

            var order = orderList.orders.FirstOrDefault(o => o.OrderId == orderId);

            //NULL means it was already filled BUG: OR CANCELLED!!! TODO: some better way of getting order status
            if (null == order)
            {
                return new Order(true);
            }
            return order;
        }


        internal Market GetMarketDepth()
        {
            //BIDs
            var command = new MarketDepthRequest
            {
                id = 2,
                taker_pays = new Take { currency = "XRP" },
                taker_gets = new Take { currency = _fiatCurreny, issuer = _issuerAddress }
            };

            string bidData = sendToRippleNet(Helpers.SerializeJson(command));
            if (null == bidData)
            {
                return null;
            }

            if (!checkError("GetMarketDepth", bidData))
            {
                return null;
            }

            var bids = Helpers.DeserializeJSON<MarketDepthBidsResponse>(bidData);
            if (null == bids.result || null == bids.result.offers)
            {
                _logger.AppendMessage("bidData JSON is " + Environment.NewLine + bidData, true, ConsoleColor.Magenta);
                return null;
            }

            //ASKs
            command = new MarketDepthRequest
            {
                id = 3,
                taker_pays = new Take { currency = _fiatCurreny, issuer = _issuerAddress },
                taker_gets = new Take { currency = "XRP" }
            };

            string askData = sendToRippleNet(Helpers.SerializeJson(command));
            if (null == askData)
            {
                return null;
            }

            if (!checkError("GetMarketDepth", askData))
            {
                return null;
            }

            var asks = Helpers.DeserializeJSON<MarketDepthAsksResponse>(askData);
            if (null == asks.result || null == asks.result.offers)
            {
                _logger.AppendMessage("askData JSON is " + Environment.NewLine + askData, true, ConsoleColor.Magenta);
                return null;
            }

            var market = new Market
            {
                Bids  = bids.result.offers,
                Asks = asks.result.offers
            };

            return market;
        }

        /// <summary>
        /// Get only ask offers of a market. 
        /// </summary>
        /// <param name="counterAssetCode">Code of counter asset (must not be XRP)</param>
        /// <param name="counterAssetGateway">Gateway address of counter asset</param>
        internal List<FiatAsk> GetOrderBookAsks(string counterAssetCode, string counterAssetGateway, bool includeAutoBridged = false)
        {
            var command = new MarketDepthRequest
            {
                id = 2,
                taker_pays = new Take { currency = counterAssetCode, issuer = counterAssetGateway },
                taker_gets = new Take { currency = _fiatCurreny, issuer = _issuerAddress }
            };

            string askData = sendToRippleNet(Helpers.SerializeJson(command));
            if (null == askData)
            {
                return null;
            }

            if (!checkError(String.Format("GetOrderBookAsks({0}, {1})", counterAssetCode, counterAssetGateway), askData))
            {
                return null;
            }

            var asks = Helpers.DeserializeJSON<MarketDepthFiatAsksResponse>(askData);
            if (null == asks.result)
            {
                _logger.AppendMessage("askData JSON is " + Environment.NewLine + askData, true, ConsoleColor.Magenta);
                return null;
            }

            if (!includeAutoBridged)
            {
                return asks.result.offers;
            }

            //Check auto-bridged orders by taking asset1/XRP and XRP/asset2 and counting asset1/asset2
            command = new MarketDepthRequest
            {
                id = 4,
                taker_pays = new Take { currency = counterAssetCode, issuer = counterAssetGateway },
                taker_gets = new Take { currency = Const.NATIVE_ASSET }
            };

            string natAskData = sendToRippleNet(Helpers.SerializeJson(command));
            if (null == natAskData)
            {
                return asks.result.offers;
            }

            if (!checkError(String.Format("#2 GetOrderBookAsks: orders for {0}.{1}", counterAssetCode, counterAssetGateway), natAskData))
            {
                return asks.result.offers;
            }

            var natAsks = Helpers.DeserializeJSON<MarketDepthAsksResponse>(natAskData);
            if (null == natAsks.result)
            {
                _logger.AppendMessage("natAskData JSON is " + Environment.NewLine + natAskData, true, ConsoleColor.Magenta);
                return asks.result.offers;
            }

            command = new MarketDepthRequest
            {
                id = 5,
                taker_pays = new Take { currency = Const.NATIVE_ASSET },
                taker_gets = new Take { currency = _fiatCurreny, issuer = _issuerAddress }
            };

            string natBidData = sendToRippleNet(Helpers.SerializeJson(command));
            if (null == natBidData)
            {
                return asks.result.offers;
            }

            if (!checkError(String.Format("#3 GetOrderBookAsks: orders for {0}.{1}", _fiatCurreny, _issuerAddress), natBidData))
            {
                return asks.result.offers;
            }

            var natBids = Helpers.DeserializeJSON<MarketDepthBidsResponse>(natBidData);
            if (null == natBids.result)
            {
                _logger.AppendMessage("natBidData JSON is " + Environment.NewLine + natBidData, true, ConsoleColor.Magenta);
                return asks.result.offers;
            }

            //NOTE: takes only "best" auto-bridged order and only over XRP. Considering other options would make this code
            //      too complicated and the benefits are questionable
            Ask bestAsk = natAsks.result.offers[0];
            Bid bestBid = natBids.result.offers[0];

            double abPrice = bestAsk.Price / bestBid.Price;

            //Inject the auto-bridged order to the resulting list
            for (int i = 0; i < asks.result.offers.Count; i++)
            {
                if (abPrice < asks.result.offers[i].Price)
                {
                    var abOrder = new FiatAsk
                    {
                        Account = "AUTOBRIDGED",
                        TakerPays = new Take { value = abPrice.ToString() },
                        TakerGets = new Take { value = (1.0).ToString() }      //NOTE: ugly workaround for the .Price getter
                        //Don't need anything else for trading purposes
                    };
                    asks.result.offers.Insert(i, abOrder);
                    break;
                }
            }

            return asks.result.offers;
        }

        internal int PlaceBuyOrder(double price, double amount)
        {
            long amountXrpDrops = (long) Math.Round(amount * Const.DROPS_IN_NATIVE);
            double amountFiat = price * amount;

            var command = new CreateBuyOrderRequest
            {
                tx_json = new CrOR_TxJson
                {
                    Account = _walletAddress,
                    TakerGets = new Take
                    {
                        currency = _fiatCurreny,
                        value = amountFiat.ToString("0.00000"),
                        issuer = _issuerAddress
                    },
                    TakerPays = amountXrpDrops.ToString()
                },
                secret = Configuration.SecretKey
            };

            ErrorResponse error = null;
            var delay = RETRY_DELAY;
            for (int i = 1; i <= RETRY_COUNT; i++)
            {
                var data = sendToRippleNet(Helpers.SerializeJson(command));

                if (null == data)
                {
                    return -1;
                }

                error = Helpers.DeserializeJSON<ErrorResponse>(data);
                if (!String.IsNullOrEmpty(error.error))
                {
                    _logger.AppendMessage("Error creating BUY order. Mesage=" + error.error_message, true, ConsoleColor.Magenta);
                    if (!error.IsCritical)
                    {
                        //The request might have been successfull even if server says there were problems
                        _logger.AppendMessage("Retry in " + delay + " ms...", true, ConsoleColor.Yellow);
                        delay *= 2;
                        Thread.Sleep(delay);
                        continue;
                    }
                    throw new Exception(error.error + " " + error.error_message);
                }

                var response = Helpers.DeserializeJSON<NewOrderResponse>(data);

                if (ResponseKind.FatalError == response.result.ResponseKind)        //TODO: this checking logic is to generic to be in 'PlaceBuyOrder'. 
                {
                    var message = String.Format("Error creating BUY order. Response={0} {1}", response.result.engine_result, response.result.engine_result_message);
                    _logger.AppendMessage(message, true, ConsoleColor.Yellow);
                    throw new Exception(message);
                }
                if (ResponseKind.NonCriticalError == response.result.ResponseKind)
                {
                    _logger.AppendMessage("Non-fatal error creating BUY order. Message=" + response.result.engine_result_message, true, ConsoleColor.Yellow);
                    _logger.AppendMessage("Retry in " + delay + " ms...", true, ConsoleColor.Yellow);
                    delay *= 2;
                    Thread.Sleep(delay);
                    continue;
                }

                return response.result.tx_json.Sequence;
            }

            throw new Exception(String.Format("Socket request failed {0} times in a row with error '{1}'. Giving up.", RETRY_COUNT, error.error_message));
        }

        /// <summary>Update BUY order by re-creating it. Returns new order ID.</summary>
        internal int UpdateBuyOrder(int orderId, double price, double amount)
        {
            //Cancel the old order, recreate
            if (CancelOrder(orderId))
            {
                var id = PlaceBuyOrder(price, amount);
                if (-1 == id)
                {
                    return orderId;
                }
                return id;
            }

            return orderId;
        }

        internal int PlaceSellOrder(double price, ref double amountXrp)
        {
            double amountFiat = price * amountXrp;
            return placeSellOrder(amountFiat, ref amountXrp);
        }

        private int placeSellOrder(double amountFiat, ref double amountXrp)
        {
            long amountXrpDrops = (long)Math.Round(amountXrp * Const.DROPS_IN_NATIVE);

            var command = new CreateSellOrderRequest
            {
                tx_json = new CSOR_TxJson
                {
                    Account = _walletAddress,
                    TakerPays = new Take
                    {
                        currency = _fiatCurreny,
                        value = amountFiat.ToString("0.00000"),
                        issuer = _issuerAddress
                    },
                    TakerGets = amountXrpDrops.ToString()
                },
                secret = Configuration.SecretKey
            };

            ErrorResponse error = null;
            var delay = RETRY_DELAY;
            for (int i = 1; i <= RETRY_COUNT; i++)
            {
                var data = sendToRippleNet(Helpers.SerializeJson(command));

                if (null == data)
                {
                    return -1;
                }

                error = Helpers.DeserializeJSON<ErrorResponse>(data);
                if (!String.IsNullOrEmpty(error.error))
                {
                    _logger.AppendMessage("Error creating SELL order. Mesage=" + error.error_message, true, ConsoleColor.Magenta);
                    if (!error.IsCritical)
                    {
                        _logger.AppendMessage("Retry in " + delay + " ms...", true, ConsoleColor.Yellow);
                        delay *= 2;
                        Thread.Sleep(delay);
                        continue;
                    }
                    throw new Exception(error.error + " " + error.error_message);
                }

                var response = Helpers.DeserializeJSON<NewOrderResponse>(data);

                if (ResponseKind.FatalError == response.result.ResponseKind)
                {
                    var message = String.Format("Error creating SELL order. Response={0} {1}", response.result.engine_result, response.result.engine_result_message);
                    _logger.AppendMessage(message, true, ConsoleColor.Yellow);
                    throw new Exception(message);
                }
                if (ResponseKind.NonCriticalError == response.result.ResponseKind)
                {
                    _logger.AppendMessage("Non-fatal error creating SELL order. Message=" + response.result.engine_result_message, true, ConsoleColor.Yellow);
                    _logger.AppendMessage("Retry in " + delay + " ms...", true, ConsoleColor.Yellow);
                    delay *= 2;
                    Thread.Sleep(delay);
                    continue;
                }

                return response.result.tx_json.Sequence;
            }

            throw new Exception(String.Format("Socket request failed {0} times in a row with error '{1}'. Giving up.", RETRY_COUNT, error.error_message));
        }

        /// <summary>Update SELL order by re-creating it. Returns new order ID.</summary>
        internal int UpdateSellOrder(int orderId, double price, ref double amount)
        {
            //First try to cancel the old order. Recreate it then.
            if (CancelOrder(orderId))
            {
                var id = PlaceSellOrder(price, ref amount);
                if (-1 == id) //Socket problem
                {
                    return orderId;
                }
                return id;
            }

            return orderId;
        }

        /// <summary>Place order to trade one non-XRP asset for another.</summary>
        /// <param name="amount">Amount of asset this bot owns</param>        
        /// <param name="price">Unit price</param>
        /// <param name="toCurrency">Code of asset we want to purchase</param>
        /// <param name="toGateway">Destination gateway address</param>
        /// <returns>Order ID</returns>
        internal int PlaceOrder(double amount, double price, string toCurrency, string toGateway)
        {
            double takerPaysValue = amount * price;

            var command = new CreateOrderRequest
            {
                tx_json = new CreateOrder_TxJson
                {
                    Account = _walletAddress,
                    TakerGets = new Take
                    {
                        currency = _fiatCurreny,
                        value = amount.ToString("0.############"),  //12 decimal places should be enough
                        issuer = _issuerAddress,
                    },
                    TakerPays = new Take
                    {
                        currency = toCurrency,
                        value = takerPaysValue.ToString("0.############"),
                        issuer = toGateway
                    }
                },
                secret = Configuration.SecretKey
            };

            //TODO: ugly copy pasta here (from PlaceBuyOrder). Refactor!
            ErrorResponse error = null;
            var delay = RETRY_DELAY;
            for (int i = 1; i <= RETRY_COUNT; i++)
            {
                var data = sendToRippleNet(Helpers.SerializeJson(command));

                if (null == data)
                    return -1;

                error = Helpers.DeserializeJSON<ErrorResponse>(data);
                if (!String.IsNullOrEmpty(error.error))
                {
                    _logger.AppendMessage("Error creating order. Mesage=" + error.error_message, true, ConsoleColor.Magenta);
                    if (!error.IsCritical)
                    {
                        //The request might have been successfull even if server says there were problems
                        _logger.AppendMessage("Retry in " + delay + " ms...", true, ConsoleColor.Yellow);
                        delay *= 2;
                        Thread.Sleep(delay);
                        continue;
                    }
                    throw new Exception(error.error + " " + error.error_message);
                }

                var response = Helpers.DeserializeJSON<NewOrderResponse>(data);

                if (ResponseKind.FatalError == response.result.ResponseKind)
                {
                    var message = String.Format("Error creating order. Response={0} {1}", response.result.engine_result, response.result.engine_result_message);
                    _logger.AppendMessage(message, true, ConsoleColor.Yellow);
                    throw new Exception(message);
                }
                if (ResponseKind.NonCriticalError == response.result.ResponseKind)
                {
                    _logger.AppendMessage("Non-fatal error creating order. Message=" + response.result.engine_result_message, true, ConsoleColor.Yellow);
                    _logger.AppendMessage("Retry in " + delay + " ms...", true, ConsoleColor.Yellow);
                    delay *= 2;
                    Thread.Sleep(delay);
                    continue;
                }

                return response.result.tx_json.Sequence;
            }

            throw new Exception(String.Format("Socket request failed {0} times in a row with error '{1}'. Giving up.", RETRY_COUNT, error.error_message));
        }

        /// <summary>Cancel existing offer</summary>
        /// <param name="orderId">Sequence number of order to cancel</param>
        /// <param name="verify">If true, get order data after cancellation claimed OK, to verify it indeed was cancelled successfully</param>
        /// <returns>True on success, false on fail</returns>
        internal bool CancelOrder(int orderId, bool verify = true)
        {
            var command = new CancelOrderRequest
            {                
                tx_json = new CaOR_TxJson
                {
                    Account = _walletAddress,
                    OfferSequence = orderId.ToString()
                },
                secret = Configuration.SecretKey
            };

            var data = sendToRippleNet(Helpers.SerializeJson(command));
            if (null == data) //Socket problem
            {
                return false;
            }

            //Check for error
            var error = Helpers.DeserializeJSON<ErrorResponse>(data);
            if (!String.IsNullOrEmpty(error.error))
            {
                _logger.AppendMessage(String.Format("Error cancelling order ID={0}. Reason={1} : {2}", orderId, error.error, error.error_message),
                                      true, ConsoleColor.Magenta);
                return false;
            }

            var cancel = Helpers.DeserializeJSON<CancelOrderResponse>(data);

            //Some asserts for meaningfull reply
            if (null == cancel)
            {
                _logger.AppendMessage("cancel == NULL", true, ConsoleColor.Magenta);
                return false;
            }
            if (null == cancel.result)
            {
                _logger.AppendMessage("cancel.result == NULL", true, ConsoleColor.Magenta);
                return false;
            }

            if (!cancel.result.ResultOK)
            {
                throw new Exception(String.Format("Unexpected response when canceling order {0}. _result={1}; _result_message={2}",
                                                  orderId, cancel.result.engine_result, cancel.result.engine_result_message));
            }

            return true;
        }

        /// <summary>Get recent trades of asset pair</summary>
        internal ExchangeHistoryResponse GetTradeStatistics(string baseAssetCode, string gaseAssetGateway, string counterAsset, string counterAssetGateway)
        {
            var webClient = new WebClient2(_logger, DATA_TIMEOUT);

            string assetDef1 = baseAssetCode;
            if (!String.IsNullOrEmpty(gaseAssetGateway))
            {
                assetDef1 += "+" + gaseAssetGateway;
            }
            string assetDef2 = counterAsset;
            if (!String.IsNullOrEmpty(counterAssetGateway))
            {
                assetDef2 += "+" + counterAssetGateway;
            }

            const int limit = 10;

            var url = String.Format("{0}/exchanges/{1}/{2}?descending=true&limit={3}&result=tesSUCCESS&type=OfferCreate",
                                    _dataApiUrl, assetDef1, assetDef2, limit);

            var data = webClient.DownloadObject<ExchangeHistoryResponse>(url);
            return data;
        }

        /// <summary>
        /// Cancel all orders that are not maintained by this bot and not placed manually
        /// </summary>
        internal void CleanupZombies(int buyOrderId, int sellOrderId)
        {
            AccountOrdersResponse offerList = getActiveOrders2();
            if (null == offerList)
            {
                return;
            }

            foreach (Order order in offerList.orders)
            {
                double aPrice = order.BuyPrice(order.BaseAsset, order.BaseGateway);
                if (aPrice.ToString().Contains(MANUAL_ORDER_SIGN) ||
                    (1.0 / aPrice).ToString().Contains(MANUAL_ORDER_SIGN) ||
                    order.Amount(order.BaseAsset, order.BaseGateway).ToString().Contains(MANUAL_ORDER_SIGN) ||
                    order.Amount(order.CounterAsset, order.CounterGateway).ToString().Contains(MANUAL_ORDER_SIGN))
                {
                    //TODO: This is really stupid!! Find some way how to safely flag manual/bot orders
                    _logger.AppendMessage("Cleanup: Order ID=" + order.OrderId + " not a zombie, possibly manual", true, ConsoleColor.Cyan);
                }
                else if ((-1 != buyOrderId && buyOrderId == order.OrderId) || (-1 != sellOrderId && sellOrderId == order.OrderId))
                {
                    //Our own buy/sell order
                    continue;
                }
                else
                {
                    _logger.AppendMessage(String.Format("Identified zombie order with ID={0} ({1} {2} {3} for {4} {5}). Trying to cancel...",
                                                        order.OrderId, order.specification.direction, order.Amount(order.BaseAsset, order.BaseGateway), order.BaseAsset,
                                                        order.BuyPrice(order.BaseAsset, order.BaseGateway), order.CounterAsset), true, ConsoleColor.Yellow);
                    //Found offer abandoned by this bot, try to cancel it
                    if (CancelOrder(order.OrderId))
                    {
                        _logger.AppendMessage("... success", true, ConsoleColor.Cyan);
                    }
                    else
                    {
                        _logger.AppendMessage("... failed. Maybe next time", true, ConsoleColor.Yellow);
                    }
                }
            }
        }

        /// <summary>Close the underlying socket</summary>
        internal void Close()
        {
            _closedByUser = true;
            if (_open)
            {
                _webSocket.Close();
            }
            _open = false;
        }

        ~RippleApi()
        {
            Close();
        }

        #region private helpers

        private OffersResponse getActiveOrders()        //TODO: rework to use DATA API instead of websockets
        {
            var command = new OrderInfoRequest { id = 1, account = _walletAddress };

            var data = sendToRippleNet(Helpers.SerializeJson(command));
            if (null == data)
            {
                return null;
            }

            if (!checkError("GetOrderInfo", data))
            {
                return null;
            }

            //Because 'taker_gets' and 'taker_pays' can be of 2 types, do a tricky conversion (decimal to object) to ease further processing
            var dataFix = _offerPattern.Replace(data, "'taker_${verb}s': {'currency': 'XRP', 'issuer':'ripple labs', 'value': '${value}'}".Replace("'", "\""));

            return Helpers.DeserializeJSON<OffersResponse>(dataFix);
        }

        //TODO: we'll probably need to drop this one because data API seems to give old data (probably last closed ledger)
        private AccountOrdersResponse getActiveOrders2()
        {
            var webClient = new WebClient2(_logger, DATA_TIMEOUT);
            var url = String.Format("{0}/accounts/{1}/orders", _dataApiUrl, _walletAddress);

            AccountOrdersResponse data = webClient.DownloadObject<AccountOrdersResponse>(url);

            if (null == data || null == data.orders)      //TODO: better info when server returns error?
            {
                return null;
            }

            return data;
        }

        private string sendToRippleNet(string commandData)
        {
            try
            {
                _webSocket.Send(commandData);
            }
            catch (Exception ex)
            {
                _logger.AppendMessage("Exception while sending data to socket: " + ex.Message, true, ConsoleColor.Magenta);
                if (ex.Message.Contains("The socket is not connected!"))
                {
                    //Try to resolve the connection problem; wait a while and re-open socket
                    const int sleepTimeMs = 60*1000;
                    _logger.AppendMessage("Sleep for " + sleepTimeMs + " ms, then try to reconect", true, ConsoleColor.Yellow);
                    Thread.Sleep(sleepTimeMs);
                    _webSocket.Close();
                }
                else throw;
            }

            if (!_open)
            {
                throw new InvalidOperationException("WebSocket not open");
            }

            var duration = 0;
            while (null == _lastResponse)
            {
                const int wait = 50;
                Thread.Sleep(wait);
                duration += wait;
                if (duration > SOCKET_TIMEOUT)
                {
                    _logger.AppendMessage("Didn't recieve response from socket in " + duration + " ms. Returning NULL.");
                    return _lastResponse = null;
                }
            }

            var ret = _lastResponse;
            _logger.LastResponse = ret;
            _lastResponse = null;
            return ret;
        }

        private bool checkError(string action, string data)
        {
            var error = Helpers.DeserializeJSON<ErrorResponse>(data);
            if (!String.IsNullOrEmpty(error.error))
            {
                if (!error.IsCritical)          //TODO: or check for "insufficient fee" response
                {
                    _logger.AppendMessage(action + ": non-critical error " + error.error_message, true, ConsoleColor.Yellow);
                    return false;
                }
                throw new Exception(error.error + " " + error.error_message);
            }

            return true;
        }

        private void websocket_MessageReceived(object sender, MessageReceivedEventArgs mrea)
        {
            _lastResponse = mrea.Message;
        }

        private void websocket_Opened(object sender, EventArgs e)
        {
            _logger.AppendMessage("Established WebSocket connection to " + _rippleSocketUri, true, ConsoleColor.Yellow);
            _open = true;
        }

        private void websocket_Error(object sender, SuperSocket.ClientEngine.ErrorEventArgs eea)
        {
            _logger.AppendMessage("WebSocket error: " + eea.Exception.Message, true, ConsoleColor.Yellow);
        }        

        private void websocket_Closed(object sender, EventArgs e)
        {
            _open = false;

            if (!_closedByUser)
            {
                _logger.AppendMessage("WebSocket connection was closed. Trying to reopen...", true, ConsoleColor.Yellow);
                Init();
            }
        }
        #endregion
    }
}
