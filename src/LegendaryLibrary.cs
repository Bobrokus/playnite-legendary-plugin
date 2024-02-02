﻿using CliWrap;
using CliWrap.Buffered;
using LegendaryLibraryNS.Enums;
using LegendaryLibraryNS.Models;
using LegendaryLibraryNS.Services;
using Playnite.Common;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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
            new LibraryPluginProperties { CanShutdownClient = false, HasSettings = true },
            new LegendaryClient(),
            LegendaryLauncher.Icon,
            (_) => new LegendaryLibrarySettingsView(),
            api)
        {
            Instance = this;
            SettingsViewModel = new LegendaryLibrarySettingsViewModel(this, api);
            Load3pLocalization();
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
                if (app.Is_dlc && app.Executable.IsNullOrEmpty())
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

        internal async Task<List<GameMetadata>> GetLibraryGames(CancellationToken cancelToken)
        {
            var cacheDir = GetCachePath("catalogcache");
            var games = new List<GameMetadata>();
            var accountApi = new EpicAccountClient(PlayniteApi, LegendaryLauncher.TokensPath);
            var assets = await accountApi.GetAssets();
            if (!assets?.Any() == true)
            {
                Logger.Warn("Found no assets on Epic accounts.");
            }

            var playtimeItems = await accountApi.GetPlaytimeItems();
            var gamesSettings = LegendaryGameSettingsView.LoadSavedGamesSettings();
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

                if ((catalogItem?.categories?.Any(a => a.path == "addons") == true) && (catalogItem.categories.Any(a => a.path == "addons/launchable") == false))
                {
                    continue;
                }

                if ((catalogItem?.customAttributes?.PartnerLinkType != null) && (catalogItem?.customAttributes.PartnerLinkType.value == "ubisoft"))
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

                var gameSettings = new GameSettings();
                if (gamesSettings.ContainsKey(newGame.GameId))
                {
                    gameSettings = gamesSettings[newGame.GameId];
                }
                var playtimeSyncEnabled = GetSettings().SyncPlaytime;
                if (gameSettings.AutoSyncPlaytime != null)
                {
                    playtimeSyncEnabled = (bool)gameSettings.AutoSyncPlaytime;
                }
                if (playtimeSyncEnabled)
                {
                    var playtimeItem = playtimeItems?.FirstOrDefault(x => x.artifactId == gameAsset.appName);
                    if (playtimeItem != null)
                    {
                        newGame.Playtime = playtimeItem.totalTime;
                    }
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
                    var libraryGames = GetLibraryGames(args.CancelToken).GetAwaiter().GetResult();
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

        public void StopDownloadManager()
        {
            LegendaryDownloadManager downloadManager = GetLegendaryDownloadManager();
            var runningAndQueuedDownloads = downloadManager.downloadManagerData.downloads.Where(i => i.status == DownloadStatus.Running
                                                                                                     || i.status == DownloadStatus.Queued).ToList();
            if (runningAndQueuedDownloads.Count > 0)
            {
                foreach (var download in runningAndQueuedDownloads)
                {
                    if (download.status == DownloadStatus.Running)
                    {
                        downloadManager.gracefulInstallerCTS?.Cancel();
                        downloadManager.gracefulInstallerCTS?.Dispose();
                        downloadManager.forcefulInstallerCTS?.Dispose();
                    }
                    download.status = DownloadStatus.Paused;
                }
                downloadManager.SaveData();
            }
        }

        public override async void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            var globalSettings = GetSettings();
            if (globalSettings != null)
            {
                if (globalSettings.GamesUpdatePolicy != UpdatePolicy.GameLaunch && globalSettings.GamesUpdatePolicy != UpdatePolicy.Never)
                {
                    var nextGamesUpdateTime = globalSettings.NextGamesUpdateTime;
                    if (nextGamesUpdateTime != 0)
                    {
                        DateTimeOffset now = DateTime.UtcNow;
                        if (now.ToUnixTimeSeconds() >= nextGamesUpdateTime)
                        {
                            globalSettings.NextGamesUpdateTime = GetNextUpdateCheckTime(globalSettings.GamesUpdatePolicy);
                            SavePluginSettings(globalSettings);
                            LegendaryUpdateController legendaryUpdateController = new LegendaryUpdateController();
                            var gamesUpdates = await legendaryUpdateController.CheckAllGamesUpdates();
                            if (gamesUpdates.Count > 0)
                            {
                                if (globalSettings.AutoUpdateGames)
                                {
                                    legendaryUpdateController.UpdateGame(gamesUpdates, "", true);
                                }
                                else
                                {
                                    Window window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
                                    {
                                        ShowMaximizeButton = false,
                                    });
                                    window.DataContext = gamesUpdates;
                                    window.Title = $"{ResourceProvider.GetString(LOC.Legendary3P_PlayniteExtensionsUpdates)}";
                                    window.Content = new LegendaryUpdater();
                                    window.Owner = PlayniteApi.Dialogs.GetCurrentAppWindow();
                                    window.SizeToContent = SizeToContent.WidthAndHeight;
                                    window.MinWidth = 600;
                                    window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                                    window.ShowDialog();
                                }
                            }
                        }
                    }
                }
                if (globalSettings.LauncherUpdatePolicy != UpdatePolicy.Never && LegendaryLauncher.IsInstalled)
                {
                    var nextLauncherUpdateTime = globalSettings.NextLauncherUpdateTime;
                    if (nextLauncherUpdateTime != 0)
                    {
                        DateTimeOffset now = DateTime.UtcNow;
                        if (now.ToUnixTimeSeconds() >= nextLauncherUpdateTime)
                        {
                            globalSettings.NextLauncherUpdateTime = GetNextUpdateCheckTime(globalSettings.LauncherUpdatePolicy);
                            SavePluginSettings(globalSettings);
                            var versionInfoContent = await LegendaryLauncher.GetVersionInfoContent();
                            if (versionInfoContent.release_info != null)
                            {
                                var newVersion = versionInfoContent.release_info.version;
                                var oldVersion = await LegendaryLauncher.GetLauncherVersion();
                                if (oldVersion != "0" && newVersion != oldVersion)
                                {
                                    var options = new List<MessageBoxOption>
                                    {
                                        new MessageBoxOption(ResourceProvider.GetString(LOC.LegendaryViewChangelog), true),
                                        new MessageBoxOption(ResourceProvider.GetString(LOC.Legendary3P_PlayniteOKLabel), false, true),
                                    };
                                    var result = PlayniteApi.Dialogs.ShowMessage(string.Format(ResourceProvider.GetString(LOC.LegendaryNewVersionAvailable), "Legendary Launcher", newVersion), ResourceProvider.GetString(LOC.Legendary3P_PlayniteUpdaterWindowTitle), MessageBoxImage.Information, options);
                                    if (result == options[0])
                                    {
                                        var changelogURL = versionInfoContent.release_info.gh_url;
                                        Playnite.Commands.GlobalCommands.NavigateUrl(changelogURL);
                                    }
                                }
                            }
                        }
                    }

                }
            }
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            StopDownloadManager();
            var settings = GetSettings();
            if (settings != null)
            {
                if (settings.AutoClearCache != ClearCacheTime.Never)
                {
                    var cacheDirs = new List<string>()
                    {
                        GetCachePath("catalogcache"),
                        GetCachePath("infocache"),
                        GetCachePath("sdlcache"),
                        Path.Combine(LegendaryLauncher.ConfigPath, "manifests"),
                        Path.Combine(LegendaryLauncher.ConfigPath, "metadata")
                    };

                    var nextClearingTime = settings.NextClearingTime;
                    if (nextClearingTime != 0)
                    {
                        DateTimeOffset now = DateTime.UtcNow;
                        if (now.ToUnixTimeSeconds() >= nextClearingTime)
                        {
                            foreach (var cacheDir in cacheDirs)
                            {
                                if (Directory.Exists(cacheDir))
                                {
                                    Directory.Delete(cacheDir, true);
                                }
                            }
                            settings.NextClearingTime = GetNextClearingTime(settings.AutoClearCache);
                            SavePluginSettings(settings);
                        }
                    }
                    else
                    {
                        settings.NextClearingTime = GetNextClearingTime(settings.AutoClearCache);
                        SavePluginSettings(settings);
                    }
                }
            }
        }

       public static long GetNextUpdateCheckTime(UpdatePolicy frequency)
       {
            DateTimeOffset? updateTime = null;
            DateTimeOffset now = DateTime.UtcNow;
            switch (frequency)
            {
                case UpdatePolicy.PlayniteLaunch:
                    updateTime = now;
                    break;
                case UpdatePolicy.Day:
                    updateTime = now.AddDays(1);
                    break;
                case UpdatePolicy.Week:
                    updateTime = now.AddDays(7);
                    break;
                case UpdatePolicy.Month:
                    updateTime = now.AddMonths(1);
                    break;
                case UpdatePolicy.ThreeMonths:
                    updateTime = now.AddMonths(3);
                    break;
                case UpdatePolicy.SixMonths:
                    updateTime = now.AddMonths(6);
                    break;
                default:
                    break;
            }
            return updateTime?.ToUnixTimeSeconds() ?? 0;
        }

        public static long GetNextClearingTime(ClearCacheTime frequency)
        {
            DateTimeOffset? clearingTime = null;
            DateTimeOffset now = DateTime.UtcNow;
            switch (frequency)
            {
                case ClearCacheTime.Day:
                    clearingTime = now.AddDays(1);
                    break;
                case ClearCacheTime.Week:
                    clearingTime = now.AddDays(7);
                    break;
                case ClearCacheTime.Month:
                    clearingTime = now.AddMonths(1);
                    break;
                case ClearCacheTime.ThreeMonths:
                    clearingTime = now.AddMonths(3);
                    break;
                case ClearCacheTime.SixMonths:
                    clearingTime = now.AddMonths(6);
                    break;
                default:
                    break;
            }
            return clearingTime?.ToUnixTimeSeconds() ?? 0;
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            foreach (var game in args.Games)
            {
                if (game.PluginId == Id && game.IsInstalled)
                {
                    yield return new GameMenuItem
                    {
                        Description = ResourceProvider.GetString(LOC.LegendaryMove),
                        Action = (args) =>
                        {
                            if (!LegendaryLauncher.IsInstalled)
                            {
                                throw new Exception(ResourceProvider.GetString(LOC.LegendaryLauncherNotInstalled));
                            }

                            var newPath = PlayniteApi.Dialogs.SelectFolder();
                            if (newPath != "")
                            {
                                var oldPath = game.InstallDirectory;
                                if (Directory.Exists(oldPath) && Directory.Exists(newPath))
                                {
                                    string sepChar = Path.DirectorySeparatorChar.ToString();
                                    string altChar = Path.AltDirectorySeparatorChar.ToString();
                                    if (!oldPath.EndsWith(sepChar) && !oldPath.EndsWith(altChar))
                                    {
                                        oldPath += sepChar;
                                    }
                                    var folderName = Path.GetFileName(Path.GetDirectoryName(oldPath));
                                    newPath = Path.Combine(newPath, folderName);
                                    var moveConfirm = PlayniteApi.Dialogs.ShowMessage(ResourceProvider.GetString(LOC.LegendaryMoveConfirm).Format(game.Name, newPath), ResourceProvider.GetString(LOC.LegendaryMove), MessageBoxButton.YesNo, MessageBoxImage.Question);
                                    if (moveConfirm == MessageBoxResult.Yes)
                                    {
                                        GlobalProgressOptions globalProgressOptions = new GlobalProgressOptions(ResourceProvider.GetString(LOC.LegendaryMovingGame).Format(game.Name, newPath), false);
                                        PlayniteApi.Dialogs.ActivateGlobalProgress((a) =>
                                        {
                                            a.ProgressMaxValue = 3;
                                            a.CurrentProgressValue = 0;
                                            _ = (Application.Current.Dispatcher?.BeginInvoke((Action)async delegate
                                            {
                                                try
                                                {
                                                    StopDownloadManager();
                                                    await LegendaryDownloadManager.WaitUntilLegendaryCloses();
                                                    Directory.Move(oldPath, newPath);
                                                    a.CurrentProgressValue = 1;
                                                    var rewriteResult = await Cli.Wrap(LegendaryLauncher.ClientExecPath)
                                                                                 .WithArguments(new[] { "move", game.GameId, newPath, "--skip-move" })
                                                                                 .WithEnvironmentVariables(LegendaryLauncher.DefaultEnvironmentVariables)
                                                                                 .ExecuteBufferedAsync();
                                                    var errorMessage = rewriteResult.StandardError;
                                                    if (rewriteResult.ExitCode != 0 || errorMessage.Contains("ERROR") || errorMessage.Contains("CRITICAL") || errorMessage.Contains("Error"))
                                                    {
                                                        logger.Error($"[Legendary] {errorMessage}");
                                                        logger.Error($"[Legendary] exit code: {rewriteResult.ExitCode}");
                                                    }
                                                    a.CurrentProgressValue = 2;
                                                    game.InstallDirectory = newPath;
                                                    PlayniteApi.Database.Games.Update(game);
                                                    a.CurrentProgressValue = 3;
                                                    PlayniteApi.Dialogs.ShowMessage(ResourceProvider.GetString(LOC.LegendaryMoveGameSuccess).Format(game.Name, newPath));
                                                }
                                                catch (Exception e)
                                                {
                                                    a.CurrentProgressValue = 3;
                                                    PlayniteApi.Dialogs.ShowErrorMessage(ResourceProvider.GetString(LOC.LegendaryMoveGameError).Format(game.Name, newPath));
                                                    logger.Error(e.Message);
                                                }
                                            }));
                                        }, globalProgressOptions);
                                    }
                                }
                            }
                        }
                    };
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
                            var installProperties = new DownloadProperties { downloadAction = DownloadAction.Repair };
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
                        Action = (args) =>
                        {
                            if (!LegendaryLauncher.IsInstalled)
                            {
                                throw new Exception(ResourceProvider.GetString(LOC.LegendaryLauncherNotInstalled));
                            }
                            LegendaryUpdateController legendaryUpdateController = new LegendaryUpdateController();
                            var gamesToUpdate = new Dictionary<string, Installed>();
                            GlobalProgressOptions updateCheckProgressOptions = new GlobalProgressOptions(ResourceProvider.GetString(LOC.LegendaryCheckingForUpdates), false) { IsIndeterminate = true };
                            PlayniteApi.Dialogs.ActivateGlobalProgress(async (a) =>
                            {
                                gamesToUpdate = await legendaryUpdateController.CheckGameUpdates(game.Name, game.GameId);
                            }, updateCheckProgressOptions);
                            if (gamesToUpdate.Count > 0)
                            {
                                Window window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
                                {
                                    ShowMaximizeButton = false,
                                });
                                window.DataContext = gamesToUpdate;
                                window.Title = $"{ResourceProvider.GetString(LOC.Legendary3P_PlayniteExtensionsUpdates)}";
                                window.Content = new LegendaryUpdater();
                                window.Owner = PlayniteApi.Dialogs.GetCurrentAppWindow();
                                window.SizeToContent = SizeToContent.WidthAndHeight;
                                window.MinWidth = 600;
                                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                                window.ShowDialog();
                            }
                            else
                            {
                                PlayniteApi.Dialogs.ShowMessage(ResourceProvider.GetString(LOC.LegendaryNoUpdatesAvailable), game.Name);
                            }
                        }
                    };
                    yield return new GameMenuItem
                    {
                        Description = ResourceProvider.GetString(LOC.LegendaryManageDlcs),
                        Action = (args) =>
                        {
                            if (!LegendaryLauncher.IsInstalled)
                            {
                                throw new Exception(ResourceProvider.GetString(LOC.LegendaryLauncherNotInstalled));
                            }

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
                            window.Title = $"{ResourceProvider.GetString(LOC.LegendaryManageDlcs)} - {game.Name}";
                            window.DataContext = game;
                            window.Content = new LegendaryDlcManager();
                            window.Owner = PlayniteApi.Dialogs.GetCurrentAppWindow();
                            window.SizeToContent = SizeToContent.WidthAndHeight;
                            window.MinWidth = 600;
                            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                            window.ShowDialog();
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
