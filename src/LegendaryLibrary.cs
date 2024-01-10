﻿using CliWrap;
using CliWrap.EventStream;
using LegendaryLibraryNS.Enums;
using LegendaryLibraryNS.Models;
using LegendaryLibraryNS.Services;
using Playnite.Common;
using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;

namespace LegendaryLibraryNS
{
    [LoadPlugin]
    public class LegendaryLibrary : LibraryPluginBase<LegendaryLibrarySettingsViewModel>
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        public static LegendaryLibrary Instance { get; set; }
        public static bool LegendaryGameInstaller { get; internal set; }
        public LegendaryDownloadManager LegendaryDownloadManager { get; set; }

        public LegendaryLibrary(IPlayniteAPI api) : base(
            "Legendary (Epic)",
            Guid.Parse("EAD65C3B-2F8F-4E37-B4E6-B3DE6BE540C6"),
            new LibraryPluginProperties { CanShutdownClient = true, HasSettings = true },
            new LegendaryClient(),
            LegendaryLauncher.Icon,
            (_) => new LegendaryLibrarySettingsView(),
            api)
        {
            Instance = this;
            SettingsViewModel = new LegendaryLibrarySettingsViewModel(this, api);
            Load3pLocalization();
            MigrateOnlineGames();
        }

        public static LegendaryLibrarySettings GetSettings()
        {
            return Instance.SettingsViewModel?.Settings ?? null;
        }

        public static LegendaryDownloadManager GetLegendaryDownloadManager()
        {
            if (Instance.LegendaryDownloadManager == null)
            {
                Instance.LegendaryDownloadManager = new LegendaryDownloadManager();
            }
            return Instance.LegendaryDownloadManager;
        }

        internal Dictionary<string, GameMetadata> GetInstalledGames()
        {
            var games = new Dictionary<string, GameMetadata>();
            var appList = LegendaryLauncher.GetInstalledAppList();

            foreach (KeyValuePair<string, Installed> d in appList)
            {
                var app = d.Value;

                if (app.App_name.StartsWith("UE_"))
                {
                    continue;
                }

                // DLC
                if (app.Is_dlc)
                {
                    continue;
                }

                var installLocation = app.Install_path;
                var gameName = app?.Title ?? Path.GetFileName(installLocation);
                if (installLocation.IsNullOrEmpty())
                {
                    continue;
                }

                installLocation = Paths.FixSeparators(installLocation);
                if (!Directory.Exists(installLocation))
                {
                    logger.Error($"Epic game {gameName} installation directory {installLocation} not detected.");
                    continue;
                }

                var game = new GameMetadata()
                {
                    Source = new MetadataNameProperty("Epic"),
                    GameId = app.App_name,
                    Name = gameName,
                    Version = app.Version,
                    InstallSize = (ulong?)app.Install_size,
                    InstallDirectory = installLocation,
                    IsInstalled = true,
                    Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("pc_windows") }
                };

                game.Name = game.Name.RemoveTrademarks();
                games.Add(game.GameId, game);
            }

            return games;
        }

        internal List<GameMetadata> GetLibraryGames(CancellationToken cancelToken)
        {
            var cacheDir = GetCachePath("catalogcache");
            var games = new List<GameMetadata>();
            var accountApi = new EpicAccountClient(PlayniteApi, LegendaryLauncher.TokensPath);
            var assets = accountApi.GetAssets();
            if (!assets?.Any() == true)
            {
                Logger.Warn("Found no assets on Epic accounts.");
            }

            var playtimeItems = accountApi.GetPlaytimeItems();
            foreach (var gameAsset in assets.Where(a => a.@namespace != "ue"))
            {
                if (cancelToken.IsCancellationRequested)
                {
                    break;
                }

                var cacheFile = Paths.GetSafePathName($"{gameAsset.@namespace}_{gameAsset.catalogItemId}_{gameAsset.buildVersion}.json");
                cacheFile = Path.Combine(cacheDir, cacheFile);
                var catalogItem = accountApi.GetCatalogItem(gameAsset.@namespace, gameAsset.catalogItemId, cacheFile);
                if (catalogItem?.categories?.Any(a => a.path == "applications") != true)
                {
                    continue;
                }

                if (catalogItem?.categories?.Any(a => a.path == "dlc") == true)
                {
                    continue;
                }

                var newGame = new GameMetadata
                {
                    Source = new MetadataNameProperty("Epic"),
                    GameId = gameAsset.appName,
                    Name = catalogItem.title.RemoveTrademarks(),
                    Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("pc_windows") }
                };

                var playtimeItem = playtimeItems?.FirstOrDefault(x => x.artifactId == gameAsset.appName);
                if (playtimeItem != null)
                {
                    newGame.Playtime = playtimeItem.totalTime;
                }

                games.Add(newGame);
            }

            return games;
        }

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            var allGames = new List<GameMetadata>();
            var installedGames = new Dictionary<string, GameMetadata>();
            Exception importError = null;

            if (SettingsViewModel.Settings.ImportInstalledGames)
            {
                try
                {
                    installedGames = GetInstalledGames();
                    Logger.Debug($"Found {installedGames.Count} installed Epic games.");
                    allGames.AddRange(installedGames.Values.ToList());
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Failed to import installed Epic games.");
                    importError = e;
                }
            }

            if (SettingsViewModel.Settings.ConnectAccount)
            {
                try
                {
                    var libraryGames = GetLibraryGames(args.CancelToken);
                    Logger.Debug($"Found {libraryGames.Count} library Epic games.");

                    if (!SettingsViewModel.Settings.ImportUninstalledGames)
                    {
                        libraryGames = libraryGames.Where(lg => installedGames.ContainsKey(lg.GameId)).ToList();
                    }

                    foreach (var game in libraryGames)
                    {
                        if (installedGames.TryGetValue(game.GameId, out var installed))
                        {
                            installed.Playtime = game.Playtime;
                            installed.LastActivity = game.LastActivity;
                            installed.Name = game.Name;
                        }
                        else
                        {
                            allGames.Add(game);
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Failed to import linked account Epic games details.");
                    importError = e;
                }
            }

            if (importError != null)
            {
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    ImportErrorMessageId,
                    string.Format(PlayniteApi.Resources.GetString(LOC.Legendary3P_PlayniteLibraryImportError), Name) +
                    Environment.NewLine + importError.Message,
                    NotificationType.Error,
                    () => OpenSettingsView()));
            }
            else
            {
                PlayniteApi.Notifications.Remove(ImportErrorMessageId);
            }

            return allGames;
        }

        public string GetCachePath(string dirName)
        {
            return Path.Combine(GetPluginUserDataPath(), dirName);
        }

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game.PluginId != Id)
            {
                yield break;
            }

            yield return new LegendaryInstallController(args.Game);
        }

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (args.Game.PluginId != Id)
            {
                yield break;
            }

            yield return new LegendaryUninstallController(args.Game);
        }

        public override IEnumerable<PlayController> GetPlayActions(GetPlayActionsArgs args)
        {
            if (args.Game.PluginId != Id)
            {
                yield break;
            }
            yield return new LegendaryPlayController(args.Game);
        }

        public override LibraryMetadataProvider GetMetadataDownloader()
        {
            return new EpicMetadataProvider(PlayniteApi);
        }

        public void Load3pLocalization()
        {
            var currentLanguage = PlayniteApi.ApplicationSettings.Language;
            var dictionaries = Application.Current.Resources.MergedDictionaries;

            void loadString(string xamlPath)
            {
                ResourceDictionary res = null;
                try
                {
                    res = Xaml.FromFile<ResourceDictionary>(xamlPath);
                    res.Source = new Uri(xamlPath, UriKind.Absolute);
                    foreach (var key in res.Keys)
                    {
                        if (res[key] is string locString)
                        {
                            if (locString.IsNullOrEmpty())
                            {
                                res.Remove(key);
                            }
                        }
                        else
                        {
                            res.Remove(key);
                        }
                    }
                }
                catch (Exception e)
                {
                    logger.Error(e, $"Failed to parse localization file {xamlPath}");
                    return;
                }
                dictionaries.Add(res);
            }

            var extraLocDir = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), @"Localization\third_party");
            if (!Directory.Exists(extraLocDir))
            {
                return;
            }

            var enXaml = Path.Combine(extraLocDir, "en_US.xaml");
            if (!File.Exists(enXaml))
            {
                return;
            }

            loadString(enXaml);
            if (currentLanguage != "en_US")
            {
                var langXaml = Path.Combine(extraLocDir, $"{currentLanguage}.xaml");
                if (File.Exists(langXaml))
                {
                    loadString(langXaml);
                }
            }
        }

        public void MigrateOnlineGames()
        {
            var globalSettings = GetSettings();
            if (globalSettings.OnlineList.Count > 0)
            {
                var gamesSettings = LegendaryGameSettingsView.LoadSavedGamesSettings();
                foreach (var onlineGame in globalSettings.OnlineList)
                {
                    if (!gamesSettings.ContainsKey(onlineGame))
                    {
                        gamesSettings.Add(onlineGame, new GameSettings());
                    }
                    gamesSettings[onlineGame].LaunchOffline = false;
                }
                var strConf = Serialization.ToJson(gamesSettings, true);
                var dataDir = Instance.GetPluginUserDataPath();
                var dataFile = Path.Combine(dataDir, "gamesSettings.json");
                File.WriteAllText(dataFile, strConf);
                globalSettings.OnlineList.Clear();
                File.WriteAllText(Path.Combine(dataDir, "config.json"), Serialization.ToJson(globalSettings, true));
            }
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            LegendaryCloud.SyncGameSaves(args.Game.Name, args.Game.GameId, args.Game.InstallDirectory, CloudSyncAction.Download);
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            LegendaryCloud.SyncGameSaves(args.Game.Name, args.Game.GameId, args.Game.InstallDirectory, CloudSyncAction.Upload);
        }

        public override IEnumerable<SidebarItem> GetSidebarItems()
        {
            yield return new SidebarItem
            {
                Title = ResourceProvider.GetString(LOC.LegendaryPanel),
                Icon = LegendaryLauncher.Icon,
                Type = SiderbarItemType.View,
                Opened = () => GetLegendaryDownloadManager()
            };
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            LegendaryDownloadManager downloadManager = GetLegendaryDownloadManager();
            var runningAndQueuedDownloads = downloadManager.downloadManagerData.downloads.Where(i => i.status == (int)DownloadStatus.Running
                                                                                                     || i.status == (int)DownloadStatus.Queued).ToList();
            if (runningAndQueuedDownloads.Count > 0)
            {
                foreach (var download in runningAndQueuedDownloads)
                {
                    if (download.status == (int)DownloadStatus.Running)
                    {
                        downloadManager.gracefulInstallerCTS?.Cancel();
                        downloadManager.gracefulInstallerCTS?.Dispose();
                        downloadManager.forcefulInstallerCTS?.Dispose();
                    }
                    download.status = (int)DownloadStatus.Paused;
                }
                downloadManager.SaveData();
            }

            if (GetSettings().AutoClearCache != (int)ClearCacheTime.Never)
            {
                var clearingTime = DateTime.Now;
                switch (GetSettings().AutoClearCache)
                {
                    case (int)ClearCacheTime.Day:
                        clearingTime = DateTime.Now.AddDays(-1);
                        break;
                    case (int)ClearCacheTime.Week:
                        clearingTime = DateTime.Now.AddDays(-7);
                        break;
                    case (int)ClearCacheTime.Month:
                        clearingTime = DateTime.Now.AddMonths(-1);
                        break;
                    case (int)ClearCacheTime.ThreeMonths:
                        clearingTime = DateTime.Now.AddMonths(-3);
                        break;
                    case (int)ClearCacheTime.SixMonths:
                        clearingTime = DateTime.Now.AddMonths(-6);
                        break;
                    default:
                        break;
                }
                var cacheDirs = new List<string>()
                {
                    GetCachePath("catalogcache"),
                    GetCachePath("infocache"),
                    GetCachePath("sdlcache")
                };

                foreach (var cacheDir in cacheDirs)
                {
                    if (Directory.Exists(cacheDir))
                    {
                        if (Directory.GetCreationTime(cacheDir) < clearingTime)
                        {
                            Directory.Delete(cacheDir, true);
                        }
                    }
                }
            }
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            foreach (var game in args.Games)
            {
                if (game.PluginId == Id && game.IsInstalled)
                {
                    yield return new GameMenuItem
                    {
                        Description = ResourceProvider.GetString(LOC.LegendaryRepair),
                        Action = (args) =>
                        {
                            Window window = null;
                            if (PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Desktop)
                            {
                                window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
                                {
                                    ShowMaximizeButton = false,
                                });
                            }
                            else
                            {
                                window = new Window
                                {
                                    Background = System.Windows.Media.Brushes.DodgerBlue
                                };
                            }
                            window.Title = game.Name;
                            var installProperties = new DownloadProperties { downloadAction = (int)DownloadAction.Repair };
                            var installData = new DownloadManagerData.Download { gameID = game.GameId, downloadProperties = installProperties };
                            window.DataContext = installData;
                            window.Content = new LegendaryGameInstaller();
                            window.Owner = PlayniteApi.Dialogs.GetCurrentAppWindow();
                            window.SizeToContent = SizeToContent.WidthAndHeight;
                            window.MinWidth = 600;
                            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                            window.ShowDialog();
                        }
                    };
                    yield return new GameMenuItem
                    {
                        Description = ResourceProvider.GetString(LOC.Legendary3P_PlayniteCheckForUpdates),
                        Action = async (args) =>
                        {
                            if (!LegendaryLauncher.IsInstalled)
                            {
                                throw new Exception(ResourceProvider.GetString(LOC.LegendaryLauncherNotInstalled));
                            }

                            LegendaryUpdateController legendaryUpdateController = new LegendaryUpdateController();
                            await legendaryUpdateController.UpdateGame(game.Name, game.GameId);
                        }
                    };
                    yield return new GameMenuItem
                    {
                        Description = ResourceProvider.GetString(LOC.LegendaryLauncherSettings),
                        Action = (args) =>
                        {
                            Window window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
                            {
                                ShowMaximizeButton = false
                            });
                            window.DataContext = game;
                            window.Title = $"{ResourceProvider.GetString(LOC.LegendaryLauncherSettings)} - {game.Name}";
                            window.Content = new LegendaryGameSettingsView();
                            window.Owner = PlayniteApi.Dialogs.GetCurrentAppWindow();
                            window.SizeToContent = SizeToContent.WidthAndHeight;
                            window.MinWidth = 600;
                            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                            window.ShowDialog();
                        }
                    };
                }
            }
        }

    }
}
