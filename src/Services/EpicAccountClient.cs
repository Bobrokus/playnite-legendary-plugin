﻿using CliWrap;
using CliWrap.Buffered;
using CliWrap.EventStream;
using LegendaryLibraryNS.Models;
using Playnite.Common;
using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LegendaryLibraryNS.Services
{
    public class TokenException : Exception
    {
        public TokenException(string message) : base(message)
        {
        }
    }

    public class ApiRedirectResponse
    {
        public string redirectUrl { get; set; }
        public string sid { get; set; }
        public string authorizationCode { get; set; }
    }

    public class EpicAccountClient
    {
        private ILogger logger = LogManager.GetLogger();
        private IPlayniteAPI api;
        private string tokensPath;
        private readonly string loginUrl = "https://www.epicgames.com/id/login?redirectUrl=https%3A//www.epicgames.com/id/api/redirect%3FclientId%3D34a02cf8f4414e29b15921876da36f9a%26responseType%3Dcode";
        private readonly string oauthUrl = @"";
        private readonly string accountUrl = @"";
        private readonly string assetsUrl = @"";
        private readonly string catalogUrl = @"";
        private readonly string playtimeUrl = @"";
        private const string authEncodedString = "MzRhMDJjZjhmNDQxNGUyOWIxNTkyMTg3NmRhMzZmOWE6ZGFhZmJjY2M3Mzc3NDUwMzlkZmZlNTNkOTRmYzc2Y2Y=";
        private const string userAgent = @"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/108.0.0.0 Safari/537.36 Vivaldi/5.5.2805.50";

        public EpicAccountClient(IPlayniteAPI api, string tokensPath)
        {
            this.api = api;
            this.tokensPath = tokensPath;
            var oauthUrlMask = @"https://{0}/account/api/oauth/token";
            var accountUrlMask = @"https://{0}/account/api/public/account/";
            var assetsUrlMask = @"https://{0}/launcher/api/public/assets/Windows?label=Live";
            var catalogUrlMask = @"https://{0}/catalog/api/shared/namespace/";
            var playtimeUrlMask = @"https://{0}/library/api/public/playtime/account/{1}/all";

            var loadedFromConfig = false;

            if (!loadedFromConfig)
            {
                oauthUrl = string.Format(oauthUrlMask, "account-public-service-prod03.ol.epicgames.com");
                accountUrl = string.Format(accountUrlMask, "account-public-service-prod03.ol.epicgames.com");
                assetsUrl = string.Format(assetsUrlMask, "launcher-public-service-prod06.ol.epicgames.com");
                catalogUrl = string.Format(catalogUrlMask, "catalog-public-service-prod06.ol.epicgames.com");
                playtimeUrl = string.Format(playtimeUrlMask, "library-service.live.use1a.on.epicgames.com", "{0}");
            }
        }

        public async Task Login()
        {
            var loggedIn = false;
            var apiRedirectContent = string.Empty;

            using (var view = api.WebViews.CreateView(new WebViewSettings
            {
                WindowWidth = 580,
                WindowHeight = 700,
                // This is needed otherwise captcha won't pass
                UserAgent = userAgent
            }))
            {
                view.LoadingChanged += async (s, e) =>
                {
                    var address = view.GetCurrentAddress();
                    if (address.StartsWith(@"https://www.epicgames.com/id/api/redirect"))
                    {
                        apiRedirectContent = await view.GetPageTextAsync();
                        loggedIn = true;
                        view.Close();
                    }
                };

                view.DeleteDomainCookies(".epicgames.com");
                view.Navigate(loginUrl);
                view.OpenDialog();
            }

            if (!loggedIn)
            {
                return;
            }

            if (apiRedirectContent.IsNullOrEmpty())
            {
                return;
            }

            var authorizationCode = Serialization.FromJson<ApiRedirectResponse>(apiRedirectContent).authorizationCode;
            FileSystem.DeleteFile(tokensPath);
            if (string.IsNullOrEmpty(authorizationCode))
            {
                logger.Error("Failed to get login exchange key for Epic account.");
                return;
            }

            var result = await Cli.Wrap(LegendaryLauncher.ClientExecPath)
                                  .WithArguments(new[] { "auth", "--code", authorizationCode })
                                  .WithEnvironmentVariables(LegendaryLauncher.DefaultEnvironmentVariables)
                                  .WithValidation(CommandResultValidation.None)
                                  .ExecuteBufferedAsync();
            if (result.ExitCode != 0 && !result.StandardError.Contains("Successfully"))
            {
                logger.Error($"[Legendary] Failed to authenticate with the Epic Games Store. Error: {result.StandardError}");
                return;
            }
        }

        public string GetUsername()
        {
            var tokens = LoadTokens();
            return tokens.displayName;
        }

        public async Task<bool> GetIsUserLoggedIn()
        {
            var tokens = LoadTokens();
            if (tokens == null)
            {
                return false;
            }

            try
            {
                var account = await InvokeRequest<AccountResponse>(accountUrl + tokens.account_id, tokens);
                return account.Item2.id == tokens.account_id;
            }
            catch (Exception e)
            {
                if (e is TokenException)
                {
                    await RenewTokens();
                    tokens = LoadTokens();
                    if (tokens is null)
                    {
                        return false;
                    }
                    var account = await InvokeRequest<AccountResponse>(accountUrl + tokens.account_id, tokens);
                    return account.Item2.id == tokens.account_id;
                }
                else
                {
                    logger.Error(e, "Failed to validation Epic authentication.");
                    return false;
                }
            }
        }

        public async Task<List<Asset>> GetAssets()
        {
            if (!await GetIsUserLoggedIn())
            {
                throw new Exception("User is not authenticated.");
            }

            var response = await InvokeRequest<List<Asset>>(assetsUrl, LoadTokens());
            return response.Item2;
        }

        public async Task<List<PlaytimeItem>> GetPlaytimeItems()
        {
            if (!await GetIsUserLoggedIn())
            {
                throw new Exception("User is not authenticated.");
            }

            var tokens = LoadTokens();
            var formattedPlaytimeUrl = string.Format(playtimeUrl, tokens.account_id);
            var response = await InvokeRequest<List<PlaytimeItem>>(formattedPlaytimeUrl, tokens);
            return response.Item2;
        }

        public CatalogItem GetCatalogItem(string nameSpace, string id, string cachePath)
        {
            Dictionary<string, CatalogItem> result = null;
            if (!cachePath.IsNullOrEmpty() && FileSystem.FileExists(cachePath))
            {
                try
                {
                    result = Serialization.FromJson<Dictionary<string, CatalogItem>>(FileSystem.ReadStringFromFile(cachePath));
                }
                catch (Exception e)
                {
                    logger.Error(e, "Failed to load Epic catalog cache.");
                }
            }

            if (result == null)
            {
                var url = string.Format("{0}/bulk/items?id={1}&country=US&locale=en-US", nameSpace, id);
                var catalogResponse = InvokeRequest<Dictionary<string, CatalogItem>>(catalogUrl + url, LoadTokens()).GetAwaiter().GetResult();
                result = catalogResponse.Item2;
                FileSystem.WriteStringToFile(cachePath, catalogResponse.Item1);
            }

            if (result.TryGetValue(id, out var catalogItem))
            {
                return catalogItem;
            }
            else
            {
                throw new Exception($"Epic catalog item for {id} {nameSpace} not found.");
            }
        }

        private async Task RenewTokens()
        {
            var cmd = Cli.Wrap(LegendaryLauncher.ClientExecPath)
                         .WithEnvironmentVariables(LegendaryLauncher.DefaultEnvironmentVariables)
                         .WithArguments("auth");
            using var forcefulCTS = new CancellationTokenSource();
            using var gracefulCTS = new CancellationTokenSource();
            try
            {
                await foreach (CommandEvent cmdEvent in cmd.ListenAsync(Console.OutputEncoding, Console.OutputEncoding, forcefulCTS.Token, gracefulCTS.Token))
                {
                    switch (cmdEvent)
                    {
                        case StandardErrorCommandEvent stdErr:
                            // If tokens can't be renewed, Legendary will try to open web browser
                            // and demand an answer from user,
                            // so we need to prevent that.
                            if (stdErr.Text.Contains("no longer valid"))
                            {
                                gracefulCTS.Cancel();
                                gracefulCTS?.Dispose();
                                forcefulCTS?.Dispose();
                            }
                            break;
                        case ExitedCommandEvent exited:
                            gracefulCTS?.Dispose();
                            forcefulCTS?.Dispose();
                            break;
                        default:
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Command was canceled
            }
        }

        private async Task<Tuple<string, T>> InvokeRequest<T>(string url, OauthResponse tokens) where T : class
        {
            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Clear();
                httpClient.DefaultRequestHeaders.Add("Authorization", tokens.token_type + " " + tokens.access_token);
                var response = await httpClient.GetAsync(url);
                var str = await response.Content.ReadAsStringAsync();

                if (Serialization.TryFromJson<ErrorResponse>(str, out var error) && !string.IsNullOrEmpty(error.errorCode))
                {
                    throw new TokenException(error.errorCode);
                }
                else
                {
                    try
                    {
                        return new Tuple<string, T>(str, Serialization.FromJson<T>(str));
                    }
                    catch
                    {
                        // For cases like #134, where the entire service is down and doesn't even return valid error messages.
                        logger.Error(str);
                        throw new Exception("Failed to get data from Epic service.");
                    }
                }
            }
        }

        private OauthResponse LoadTokens()
        {
            if (File.Exists(tokensPath))
            {
                try
                {
                    return Serialization.FromJson<OauthResponse>(FileSystem.ReadFileAsStringSafe(tokensPath));
                }
                catch (Exception e)
                {
                    logger.Error(e, "Failed to load saved tokens.");
                }
            }

            return null;
        }
    }
}
