﻿using System;
using System.Collections.Generic;
using System.Linq;
using Common;


namespace RippleBot
{
    /// <summary>
    /// Do arbitrage between 2 fiat currencies. Watch for over-xrp-ratio and if it changes in behoof of currency we
    /// hold (i.e. it becomes more "expensive"), do 2 trades: to XRP and then to the other fiat currency. Then wait to
    /// buy back with profit.
    /// </summary>
    public class ArbitrageTrader : TraderBase
    {
        private readonly string _baseCurrency;
        private readonly string _arbCurrency;
        private readonly string _baseGateway;
        private readonly string _arbGateway;
        private readonly double _parity;
        private const double _arbFactor = 1.007;        //The price of arbitrage currency must be at least 0.7% higher than parity to buy
        private const double _backFactor = 1.006;       //The price of basic currency must be at most 0.6% higher than parity to sell back
        private const double MIN_TRADE_VOLUME = 1.0;    //Minimum trade volume in XRP so we don't lose on fees

        private const int ZOMBIE_CHECK = 10;            //Check for dangling orders to cancel every 10th round
        private int _counter;

        //TODO: find and incorporate gateway fees (RTJ has around 1%). Load from config.
        private double _baseFeeFactor = 0.0;
        private double _arbFeeFactor = 0.00;    //0%

        private readonly RippleApi _baseRequestor;      //TODO: No! Use only one requestor with 2 gateways
        private readonly RippleApi _arbRequestor;



        public ArbitrageTrader(Logger logger)
            : base(logger)
        {
            _baseCurrency = Configuration.GetValue("base_currency_code");
            _baseGateway = Configuration.GetValue("base_gateway_address");

            _arbCurrency = Configuration.GetValue("arbitrage_currency_code");
            _arbGateway = Configuration.GetValue("arbitrage_gateway_address");

            _parity = double.Parse(Configuration.GetValue("parity_ratio"));
            _intervalMs = 8000;

            _baseRequestor = new RippleApi(logger, _baseGateway, _baseCurrency);
            _baseRequestor.Init();
            _arbRequestor = new RippleApi(logger, _arbGateway, _arbCurrency);
            _arbRequestor.Init();
            log("Arbitrage trader started for currencies {0}, {1} with parity {2:0.000}", _baseCurrency, _arbCurrency, _parity);
        }


        protected override void Check()
        {
            var baseMarket = _baseRequestor.GetMarketDepth();

            if (null == baseMarket)
                return;

            var arbMarket = _arbRequestor.GetMarketDepth();

            if (null == arbMarket)
                return;

            var baseBalance = _baseRequestor.GetBalance(_baseCurrency);
            var arbBalance = _arbRequestor.GetBalance(_arbCurrency);
            var xrpBalance = _baseRequestor.GetXrpBalance();
            log("Balances: {0:0.000} {1}; {2:0.000} {3}; {4:0.000} XRP", baseBalance, _baseCurrency, arbBalance, _arbCurrency, xrpBalance);

            var lowestBaseAskPrice = baseMarket.Asks[0].Price;
            var highestArbBidPrice = arbMarket.Bids[0].Price;
            double baseRatio = highestArbBidPrice / lowestBaseAskPrice;

            var lowestArbAskPrice = arbMarket.Asks[0].Price;
            var highestBaseBidPrice = baseMarket.Bids[0].Price;
            var arbRatio = lowestArbAskPrice / highestBaseBidPrice;

            log("BASIC ratio={0:0.00000}; ARB ratio={1:0.00000}", baseRatio, arbRatio);

            if (double.IsNaN(baseRatio) || double.IsNaN(arbRatio))
                return;     //Happens sometimes after bad JSON parsing

            //Trade from basic to arbitrage currency
            if (baseBalance >= 0.1)
            {
                if (baseRatio > _parity * _arbFactor)
                {
                    log("Chance to buy cheap {0} (BASIC ratio {1:0.00000} > {2:0.00000})", ConsoleColor.Cyan, _arbCurrency, baseRatio, _parity*_arbFactor);
                    var baseVolume = baseMarket.Asks[0].Amount;
                    var arbVolume = arbMarket.Bids[0].Amount;
                    if (baseVolume < MIN_TRADE_VOLUME || arbVolume < MIN_TRADE_VOLUME)
                        log("Insufficient volume: {0} XRP for {1}; {2} XRP for {3}", baseVolume, _baseCurrency, arbVolume, _arbCurrency);
                    else
                    {
                        //Try to buy XRP for BASIC
                        var amount = Math.Min(baseVolume, arbVolume);
                        int orderId = _baseRequestor.PlaceBuyOrder(lowestBaseAskPrice + 0.000002, amount);
                        log("Tried to buy {0} XRP for {1} {2} each. OrderID={3}", amount, lowestBaseAskPrice, _baseCurrency, orderId);
                        var orderInfo = _baseRequestor.GetOrderInfo(orderId);

                        if (orderInfo.Closed)
                        {
                            var newXrpBalance = _baseRequestor.GetXrpBalance();
                            amount = newXrpBalance - xrpBalance;
                            log("Buy XRP orderID={0} filled OK, bought {1} XRP", ConsoleColor.Green, orderId, amount);
                            amount -= 0.048;    //So we don't fall into "lack of funds" due to fees
                            //Try to sell XRP for ARB
                            var arbBuyOrderId = _arbRequestor.PlaceSellOrder(highestArbBidPrice - 0.000002, ref amount);
                            log("Tried to sell {0} XRP for {1} {2} each. OrderID={3}", amount, highestArbBidPrice, _arbCurrency, arbBuyOrderId);
                            var arbBuyOrderInfo = _arbRequestor.GetOrderInfo(arbBuyOrderId);
                            if (arbBuyOrderInfo.Closed)
                            {
                                log("Buy {0} orderID={1} filled OK", ConsoleColor.Green, _arbCurrency, arbBuyOrderId);
                                log("{0} -> {1} ARBITRAGE SUCCEEDED!", ConsoleColor.Green, _baseCurrency, _arbCurrency);
                            }
                            else
                            {
                                log("OrderID={0} (sell {1:0.000} XRP for {2} {3} each) remains dangling. Forgetting it...", ConsoleColor.Yellow,
                                    arbBuyOrderId, arbBuyOrderInfo.AmountXrp, arbBuyOrderInfo.Price, _arbCurrency);
                                //NOTE: If it's closed later, the arbitrage is just successfully finished silently
                            }
                        }
                        else    //TODO: handle partially filled offer somehow? Q: And what if it's closed later? must finish the sell-XRP-for-ARB somewhere
                        {
                            log("OrderID={0} (buy {1:0.000} XRP for {2} {3} each) remains dangling. Forgetting it...", ConsoleColor.Yellow,
                                orderId, orderInfo.AmountXrp, orderInfo.Price, _baseCurrency);
                        }
                    }
                }
            }

            if (arbBalance >= 0.1)
            {
                if (arbRatio < _parity * _backFactor)
                {
                    log("Chance to sell {0} for {1} (ARB ratio {2:0.00000} > {3:0.00000})", ConsoleColor.Cyan,
                        _arbCurrency, _baseCurrency, arbRatio, _parity*_backFactor);
                    var arbVolume = arbMarket.Asks[0].Amount;
                    var baseVolume = baseMarket.Bids[0].Amount;
                    if (arbVolume < MIN_TRADE_VOLUME || baseVolume < MIN_TRADE_VOLUME)
                        log("Insufficient volume: {0} XRP for {1}; {2} XRP for {3}", arbVolume, _arbCurrency, baseVolume, _baseCurrency);
                    else
                    {
                        //Try to buy XRP for ARB
                        var amount = Math.Min(baseVolume, arbVolume);
                        int orderId = _arbRequestor.PlaceBuyOrder(lowestArbAskPrice + 0.000002, amount);
                        log("Tried to buy {0} XRP for {1} {2} each. OrderID={3}", amount, lowestArbAskPrice, _arbCurrency, orderId);
                        var orderInfo = _arbRequestor.GetOrderInfo(orderId);

                        if (orderInfo.Closed)
                        {
                            var newXrpBalance = _arbRequestor.GetXrpBalance();
                            amount = newXrpBalance - xrpBalance;
                            log("Buy XRP orderID={0} filled OK, bought {1} XRP", ConsoleColor.Green, orderId, amount);
                            //Try to sell XRP for BASIC
                            var baseBuyOrderId = _baseRequestor.PlaceSellOrder(highestBaseBidPrice - 0.000002, ref amount);
                            log("Tried to sell {0} XRP for {1} {2} each. OrderID={3}", amount, highestBaseBidPrice, _baseCurrency, baseBuyOrderId);
                            var baseBuyOrderInfo = _baseRequestor.GetOrderInfo(baseBuyOrderId);
                            if (baseBuyOrderInfo.Closed)
                            {
                                log("Buy {0} orderID={1} filled OK", ConsoleColor.Green, _baseCurrency, baseBuyOrderId);
                                log("{0} -> {1} ARBITRAGE SUCCEEDED!", ConsoleColor.Green, _arbCurrency, _baseCurrency);
                            }
                            else
                            {
                                log("OrderID={0} (sell {1:0.000} XRP for {2} {3} each) remains dangling. Forgetting it...", ConsoleColor.Yellow,
                                    baseBuyOrderId, baseBuyOrderInfo.AmountXrp, baseBuyOrderInfo.Price, _baseCurrency);
                                //NOTE: If it's closed later, the arbitrage is just successfully finished silently
                            }
                        }
                        else    //TODO: handle partially filled offer somehow? Q: And what if it's closed later? must finish the sell-XRP-for-ARB somewhere
                        {
                            log("OrderID={0} (buy {1:0.000} XRP for {2} {3} each) remains dangling. Forgetting it...", ConsoleColor.Yellow,
                                orderId, orderInfo.AmountXrp, orderInfo.Price, _arbCurrency);
                        }
                    }
                }
            }

            if (_counter++ == ZOMBIE_CHECK)
            {
                _counter = 0;
                _baseRequestor.CleanupZombies(-1, -1);
            }

            log(new string('=', 84));
        }
    }
}