﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using CryptoGramBot.Configuration;
using CryptoGramBot.Helpers;
using CryptoGramBot.Models;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace CryptoGramBot.Services
{
    public class TelegramService
    {
        private static TelegramBotClient _bot;
        private static TelegramConfig _config;
        private static ILogger<TelegramService> _log;
        private readonly BalanceService _balanceService;
        private bool _waitingForFile;

        public TelegramService(
            TelegramConfig config,
            BalanceService balanceService,
            ILogger<TelegramService> log)
        {
            _config = config;

            _balanceService = balanceService;
            _log = log;
        }

        public async Task SendBagNotification(WalletBalance walletBalance, Trade lastTradeForPair, decimal currentPrice, decimal percentage)
        {
            var message =
                $"{DateTime.Now:g}\n" +
                $"<strong>Bag detected for {walletBalance.Currency}</strong>\n" +
                $"Bought price: {lastTradeForPair.Limit:#0.#############}\n" +
                $"Current price: {currentPrice:#0.#############}\n" +
                $"Percentage drop: {percentage}%\n" +
                $"Bought on: {lastTradeForPair.TimeStamp:g}\n" +
                $"Value: {(walletBalance.Balance * currentPrice):#0.#############}";

            await SendMessage(message, _config.ChatId);
        }

        public async Task SendMessage(string textMessage, long chatId)
        {
            await _bot.SendTextMessageAsync(chatId, textMessage, ParseMode.Html);
            _log.LogInformation("Message sent. Waiting for next command ...");
        }

        public async Task SendMessage(string textMessage)
        {
            await SendMessage(textMessage, _config.ChatId);
        }

        public async Task SendTradeNotification(Trade newTrade)
        {
            var message = $"{newTrade.TimeStamp:R}\n" +
                          $"New {newTrade.Exchange} order\n" +
                          $"<strong>{newTrade.Side} {newTrade.Base}-{newTrade.Terms}</strong>\n" +
                          $"Total: {newTrade.Cost:##0.###########} BTC\n" +
                          $"Rate: {newTrade.Limit:##0.##############} BTC";

            await SendMessage(message, _config.ChatId);
        }

        public void StartBot()
        {
            _bot = new TelegramBotClient(_config.BotToken);

            _bot.OnCallbackQuery += BotOnCallbackQueryReceived;
            _bot.OnMessage += BotOnMessageReceivedAsync;
            _bot.OnMessageEdited += BotOnMessageReceivedAsync;
            _bot.OnInlineResultChosen += BotOnChosenInlineResultReceived;
            _bot.OnReceiveError += BotOnReceiveError;

            var me = _bot.GetMeAsync().Result;
            Console.Title = me.Username;
            _bot.StartReceiving();
        }

        private async void BotOnCallbackQueryReceived(object sender, CallbackQueryEventArgs e)
        {
            await _bot.AnswerCallbackQueryAsync(e.CallbackQuery.Id,
                "Received" + e.CallbackQuery.Data);

            await CheckMessage(e.CallbackQuery.Data, e.CallbackQuery.Message.Chat.Id);
        }

        private void BotOnChosenInlineResultReceived(object sender, ChosenInlineResultEventArgs e)
        {
            Console.WriteLine($"Received choosen inline result: {e.ChosenInlineResult.ResultId}");
        }

        private async void BotOnMessageReceivedAsync(object sender, MessageEventArgs e)
        {
            var message = e.Message;

            await _bot.SendChatActionAsync(message.Chat.Id, ChatAction.Typing);

            if (_waitingForFile)
            {
                await ProcessFile(message, e.Message.Chat.Id);
                return;
            }

            if (message.Type != MessageType.TextMessage) return;

            await CheckMessage(message.Text, e.Message.Chat.Id);
        }

        private void BotOnReceiveError(object sender, ReceiveErrorEventArgs e)
        {
            _log.LogError($"Error received {e.ApiRequestException.Message}");
        }

        private async Task CheckMessage(string message, long chatId)
        {
            if (message.StartsWith("/acc"))
            {
                _log.LogInformation("Message begins with /acc. Going to split string");
                var splitString = message.Split("_");

                try
                {
                    var accountNumber = splitString[1];
                    _log.LogInformation($"pPnL check for {accountNumber}");
                    await SendPnlUpdateForAccountNumber(int.Parse(accountNumber), chatId);
                }
                catch (Exception)
                {
                    await SendHelpMessage();
                    _log.LogInformation($"Don't know what the user wants to do with the /acc. The message was {message}");
                }
            }
            else if (message.StartsWith("/profit"))
            {
                var splitString = message.Split(" ");
                _log.LogInformation("Profit details requested");

                try
                {
                    var pair = splitString[1];
                    var ccys = pair.Split('-');
                    _log.LogInformation($"User wants to check for profit for {pair}");
                    await SendProfitInfomation(chatId, ccys[0], ccys[1]);
                }
                catch (Exception)
                {
                    await SendHelpMessage();
                    _log.LogInformation($"Don't know what the user wants to do with the /profit. The message was {message}");
                }
            }
            else if (message.StartsWith("/list"))
            {
                try
                {
                    _log.LogInformation("PnL Account List request");
                    await SendAccountInfo(chatId);
                }
                catch (Exception)
                {
                    await SendHelpMessage();
                    _log.LogInformation($"Don't know what the user wants to do with the /acc. The message was {message}");
                }
            }
            else if (message.StartsWith("/excel"))
            {
                _log.LogInformation("Excel sheet");
                await SendExcelExport(chatId);
            }
            else if (message.StartsWith("/total"))
            {
                _log.LogInformation("24 Hour pnl difference");
                await SendTotalPnL(chatId);
            }
            else if (message.StartsWith("/upload_bittrex_orders"))
            {
                await SendMessage("Please upload bittrex trade export", chatId);
                _waitingForFile = true;
            }
            else
            {
                _log.LogInformation($"Don't know what the user wants to do. The message was {message}");
                await SendHelpMessage();
            }
        }

        private async Task ProcessFile(Message message, long chatId)
        {
            if (message.Document == null)
            {
                await SendMessage("Did not receive a file", chatId);
                _waitingForFile = false;
                return;
            }

            try
            {
                var file = await _bot.GetFileAsync(message.Document.FileId);
                var trades = TradeConverter.BittrexFileToTrades(file.FileStream);
                _balanceService.AddTrades(trades, out List<Trade> newTrades);
                await SendMessage($"{newTrades.Count} new bittrex trades added.", chatId);
                _waitingForFile = false;
            }
            catch (Exception)
            {
                await SendMessage("Could not process file.", chatId);
                _waitingForFile = false;
            }
        }

        private async Task SendAccountInfo(long chatId)
        {
            var accountList = await _balanceService.GetAccounts();
            var message = accountList.Aggregate($"{DateTime.Now:g}\n" + "Connected accounts on Coinigy are: \n", (current, acc) => current + "/acc_" + acc.Key + " - " + acc.Value.Name + "\n");
            _log.LogInformation("Sending the account list");
            await SendMessage(message, chatId);
        }

        private async Task SendBalanceUpdate(BalanceHistory current, BalanceHistory lastBalance, string accountName, long chatId)
        {
            var message = $"<strong>24 Hour Summary</strong> for \n<strong>{accountName}</strong>\n\n" +
                             $"{DateTime.Now:g}\n" +
                             $"<strong>Current</strong>: {current.Balance} BTC (${current.DollarAmount})\n" +
                             $"<strong>Previous</strong>: {lastBalance.Balance} BTC (${lastBalance.DollarAmount})\n" +
                             $"<strong>Difference</strong>: {(current.Balance - lastBalance.Balance):##0.###########} BTC (${Math.Round(current.DollarAmount - lastBalance.DollarAmount, 2)})\n";

            try
            {
                var percentage = Math.Round((current.Balance - lastBalance.Balance) / lastBalance.Balance * 100, 2);

                var dollarPercentage = Math.Round(
                    (current.DollarAmount - lastBalance.DollarAmount) / lastBalance.DollarAmount * 100, 2);

                message = message + $"<strong>Change</strong>: {percentage}% BTC ({dollarPercentage}% USD)";
            }
            catch (Exception ex)
            {
                await SendMessage($"Could not calculate percentages - {ex.Message}");
            }
            await SendMessage(message, chatId);
        }

        private async Task SendExcelExport(long chatId)
        {
            var tradeExport = _balanceService.GetTradeExport();
            await _bot.SendDocumentAsync(chatId, new FileToSend("TradeExport.xlsx", tradeExport.OpenRead()));
            tradeExport.Delete();
        }

        private async Task SendHelpMessage()
        {
            var usage = "Usage:\n" +
                        "/acc_n - 24 hour pnl for account number n.\n" +
                        "/list - all account names\n" +
                        "/total - 24 hour PnL for the total balance\n" +
                        "/excel - an excel export of all trades\n" +
                        "/profit BTC-XXX - profit information for pair\n" +
                        "/upload_bittrex_orders - upload bittrex order export";
            _log.LogInformation("Sending help message");
            await _bot.SendTextMessageAsync(_config.ChatId, usage,
                replyMarkup: new ReplyKeyboardRemove());
        }

        private async Task SendPnlUpdateForAccountNumber(int accountId, long chatId)
        {
            var accountBalance24HoursAgo = await _balanceService.GetAccountBalance24HoursAgo(accountId);
            await SendBalanceUpdate(accountBalance24HoursAgo.CurrentBalance, accountBalance24HoursAgo.PreviousBalance,
                accountBalance24HoursAgo.AccountName, chatId);
        }

        private async Task SendProfitInfomation(long chatId, string ccy1, string ccy2)
        {
            var profitAndLoss = await _balanceService.GetPnLInfo(ccy1, ccy2);

            var message =
                $"{DateTime.Now:g}\n" +
                $"Profit information for <strong>{ccy1 + "-" + ccy2}</strong>\n" +
                             $"<strong>Average buy price</strong>: {profitAndLoss.AverageBuyPrice:#0.###########}\n" +
                             $"<strong>Total PnL</strong>: {profitAndLoss.Profit} BTC\n";

            await SendMessage(message, chatId);
        }

        private async Task SendTotalPnL(long chatId)
        {
            var balance = await _balanceService.Get24HourTotalBalance();
            _log.LogInformation("Sending total pnl");
            await SendBalanceUpdate(balance.CurrentBalance, balance.PreviousBalance, "Total Balance", chatId);
        }
    }
}