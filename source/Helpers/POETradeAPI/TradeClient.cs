﻿using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using Sidekick.Helpers.POETradeAPI.Models;
using Sidekick.Helpers.POETradeAPI.Models.TradeData;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Sidekick.Helpers.POETradeAPI
{
    public static class TradeClient
    {
        public readonly static Uri POE_TRADE_SEARCH_BASE_URL = new Uri("https://www.pathofexile.com/trade/search/");
        public readonly static Uri POE_TRADE_EXCHANGE_BASE_URL = new Uri("https://www.pathofexile.com/trade/exchange/");
        public readonly static Uri POE_TRADE_API_BASE_URL = new Uri("https://www.pathofexile.com/api/trade/"); // TODO: Subdomain determines the language of the items.
        public readonly static Uri POE_CDN_BASE_URL = new Uri("https://web.poecdn.com/");

        public static List<League> Leagues;
        public static List<StaticItemCategory> StaticItemCategories;
        public static List<AttributeCategory> AttributeCategories;
        public static List<ItemCategory> ItemCategories;

        private static JsonSerializerSettings _jsonSerializerSettings;
        private static HttpClient _httpClient;
        private static bool IsFetching;
        private static bool OneFetchFailed;

        public static bool IsReady;

        public static League SelectedLeague;

        public static async void Initialize()
        {
            if (_jsonSerializerSettings == null)
            {
                _jsonSerializerSettings = new JsonSerializerSettings();
                _jsonSerializerSettings.Converters.Add(new StringEnumConverter { NamingStrategy = new CamelCaseNamingStrategy() });
                _jsonSerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
                _jsonSerializerSettings.NullValueHandling = NullValueHandling.Ignore;
            }

            if (IsFetching)
            {
                return;
            }

            IsFetching = true;
            Logger.Log("Fetching Path of Exile trade data.");

            _httpClient = new HttpClient();
            _httpClient.BaseAddress = POE_TRADE_API_BASE_URL;

            var fetchLeaguesTask = FetchDataAsync<League>("Leagues", "leagues");
            var fetchStaticItemCategoriesTask = FetchDataAsync<StaticItemCategory>("Static item categories", "static");
            var fetchAttributeCategoriesTask = FetchDataAsync<AttributeCategory>("Attribute categories", "stats");
            var fetchItemCategoriesTask = FetchDataAsync<ItemCategory>("Item categories", "items");

            Leagues = await fetchLeaguesTask;
            StaticItemCategories = await fetchStaticItemCategoriesTask;
            AttributeCategories = await fetchAttributeCategoriesTask;
            ItemCategories = await fetchItemCategoriesTask;

            if (OneFetchFailed)
            {
                Logger.Log("Retrying every minute.");
                Dispose();
                await Task.Run(Retry);
                return;
            }

            IsFetching = false;

            TrayIcon.PopulateLeagueSelectMenu(Leagues);

            IsReady = true;

            Logger.Log($"Path of Exile trade data fetched.");
            Logger.Log($"Sidekick is ready, press Ctrl+D over an item in-game to use. Press Escape to close overlay.");
            TrayIcon.SendNotification("Press Ctrl+D over an item in-game to use. Press Escape to close overlay.", "Sidekick is ready");
        }

        private static async void Retry()
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            if (IsFetching)
            {
                await Task.Delay(TimeSpan.FromMinutes(1));
                Retry();
            }
            else
            {
                Initialize();
            }
        }

        private static async Task<List<T>> FetchDataAsync<T>(string name, string path) where T : class
        {
            Logger.Log($"Fetching {name}.".PadLeft(4));
            List<T> result = null;

            try
            {
                var response = await _httpClient.GetAsync("data/" + path);
                var content = await response.Content.ReadAsStringAsync();
                result = JsonConvert.DeserializeObject<QueryResult<T>>(content, _jsonSerializerSettings)?.Result;
                Logger.Log($"{result.Count.ToString().PadRight(3)} {name} fetched.");
            }
            catch
            {
                OneFetchFailed = true;
                Logger.Log($"Could not fetch {name}.");
            }

            return result;
        }

        public static async Task<QueryResult<string>> Query(Item item)
        {
            Logger.Log("Querying Trade API.");
            QueryResult<string> result = null;

            try
            {
                // TODO: More complex logic for determining bulk vs regular search
                var isBulk = item.GetType() == typeof(CurrencyItem);

                StringContent body;
                if (isBulk)
                {
                    var bulkQueryRequest = new BulkQueryRequest(item);
                    body = new StringContent(JsonConvert.SerializeObject(bulkQueryRequest, _jsonSerializerSettings), Encoding.UTF8, "application/json");
                }
                else
                {
                    var queryRequest = new QueryRequest(item);
                    body = new StringContent(JsonConvert.SerializeObject(queryRequest, _jsonSerializerSettings), Encoding.UTF8, "application/json");
                }

                var response = await _httpClient.PostAsync($"{(isBulk ? "exchange" : "search")}/" + SelectedLeague.Id, body);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    result = JsonConvert.DeserializeObject<QueryResult<string>>(content);

                    var baseUri = isBulk ? TradeClient.POE_TRADE_EXCHANGE_BASE_URL : TradeClient.POE_TRADE_SEARCH_BASE_URL;
                    result.Uri = baseUri + SelectedLeague.Id + "/" + result.Id;
                }
            }
            catch
            {
                return null;
            }

            return result;

        }

        public static async Task<QueryResult<ListingResult>> GetListings(Item item)
        {
            var queryResult = await Query(item);
            if (queryResult != null)
            {
                var result = await Task.WhenAll(Enumerable.Range(0, 2).Select(x => GetListings(queryResult, x)));

                return new QueryResult<ListingResult>()
                {
                    Id = queryResult.Id,
                    Result = result.Where(x => x != null).SelectMany(x => x.Result).ToList(),
                    Total = queryResult.Total,
                    Item = item,
                    Uri = queryResult.Uri
                };
            }

            return null;
        }

        public static async Task<QueryResult<ListingResult>> GetListings(QueryResult<string> queryResult, int page = 0)
        {
            Logger.Log($"Fetching Trade API Listings from Query {queryResult.Id} page {(page + 1).ToString()}.");
            QueryResult<ListingResult> result = null;

            try
            {
                var response = await _httpClient.GetAsync("fetch/" + string.Join(",", queryResult.Result.Skip(page * 10).Take(10)) + "?query=" + queryResult.Id);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    result = JsonConvert.DeserializeObject<QueryResult<ListingResult>>(content);
                }
            }
            catch
            {
                return null;
            }


            return result;
        }


        public static void Dispose()
        {
            _httpClient.Dispose();
            _httpClient = null;

            Leagues = null;
            StaticItemCategories = null;
            AttributeCategories = null;
            ItemCategories = null;

            IsReady = false;
            IsFetching = false;
            OneFetchFailed = false;
        }
    }
}
