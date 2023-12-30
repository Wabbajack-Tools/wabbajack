﻿

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using DynamicData;
using DynamicData.Binding;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Wabbajack.Common;
using Wabbajack.Downloaders.GameFile;
using Wabbajack.DTOs;
using Wabbajack.Messages;
using Wabbajack.Networking.WabbajackClientApi;
using Wabbajack.Services.OSIntegrated;
using Wabbajack.Services.OSIntegrated.Services;

namespace Wabbajack
{
    public class ModListGalleryVM : BackNavigatingVM
    {
        public MainWindowVM MWVM { get; }

        private readonly SourceCache<ModListMetadataVM, string> _modLists = new(x => x.Metadata.NamespacedName);
        public ReadOnlyObservableCollection<ModListMetadataVM> _filteredModLists;

        public ReadOnlyObservableCollection<ModListMetadataVM> ModLists => _filteredModLists;

        private const string ALL_GAME_IDENTIFIER = "All games";

        [Reactive] public IErrorResponse Error { get; set; }

        [Reactive] public string Search { get; set; }

        [Reactive] public bool OnlyInstalled { get; set; }

        [Reactive] public bool ShowNSFW { get; set; }

        [Reactive] public bool ShowUnofficialLists { get; set; }

        [Reactive] public string GameType { get; set; }
        [Reactive] public double MinSizeFilter { get; set; }
        [Reactive] public double MaxSizeFilter { get; set; }

        [Reactive] public ModListMetadataVM MinSizeModlist { get; set; }
        [Reactive] public ModListMetadataVM MaxSizeModlist { get; set; }

        public class GameTypeEntry
        {
            public GameTypeEntry(GameMetaData gameMetaData, int amount)
            {
                GameMetaData = gameMetaData;
                IsAllGamesEntry = gameMetaData == null;
                GameIdentifier = IsAllGamesEntry ? ALL_GAME_IDENTIFIER : gameMetaData?.HumanFriendlyGameName;
                Amount = amount;
                FormattedName = IsAllGamesEntry ? $"{ALL_GAME_IDENTIFIER} ({Amount})" : $"{gameMetaData.HumanFriendlyGameName} ({Amount})";
            }
            public bool IsAllGamesEntry { get; set; }
            public GameMetaData GameMetaData { get; private set; }
            public int Amount { get; private set; }
            public string FormattedName { get; private set; }
            public string GameIdentifier { get; private set; }
            public static GameTypeEntry GetAllGamesEntry(int amount) => new(null, amount);
        }

        [Reactive] public List<GameTypeEntry> GameTypeEntries { get; set; }
        private bool _filteringOnGame;
        private GameTypeEntry _selectedGameTypeEntry = null;

        public GameTypeEntry SelectedGameTypeEntry
        {
            get => _selectedGameTypeEntry;
            set
            {
                RaiseAndSetIfChanged(ref _selectedGameTypeEntry, value == null ? GameTypeEntries?.FirstOrDefault(gte => gte.IsAllGamesEntry) : value);
                GameType = _selectedGameTypeEntry?.GameIdentifier;
            }
        }

        private readonly Client _wjClient;
        private readonly ILogger<ModListGalleryVM> _logger;
        private readonly GameLocator _locator;
        private readonly ModListDownloadMaintainer _maintainer;
        private readonly SettingsManager _settingsManager;
        private readonly CancellationToken _cancellationToken;

        public ICommand ClearFiltersCommand { get; set; }

        public ModListGalleryVM(ILogger<ModListGalleryVM> logger, Client wjClient, GameLocator locator,
            SettingsManager settingsManager, ModListDownloadMaintainer maintainer, CancellationToken cancellationToken)
            : base(logger)
        {
            var searchThrottle = TimeSpan.FromSeconds(0.5);
            _wjClient = wjClient;
            _logger = logger;
            _locator = locator;
            _maintainer = maintainer;
            _settingsManager = settingsManager;
            _cancellationToken = cancellationToken;

            ClearFiltersCommand = ReactiveCommand.Create(
                () =>
                {
                    OnlyInstalled = false;
                    ShowNSFW = false;
                    ShowUnofficialLists = false;
                    Search = string.Empty;
                    SelectedGameTypeEntry = GameTypeEntries.FirstOrDefault();
                });

            BackCommand = ReactiveCommand.Create(
                () =>
                {
                    NavigateToGlobal.Send(ScreenType.Home);
                });


            this.WhenActivated(disposables =>
            {
                LoadModLists().FireAndForget();
                LoadSettings().FireAndForget();

                Disposable.Create(() => SaveSettings().FireAndForget())
                    .DisposeWith(disposables);

                var searchTextPredicates = this.ObservableForProperty(vm => vm.Search)
                    .Throttle(searchThrottle, RxApp.MainThreadScheduler)
                    .Select(change => change.Value.Trim())
                    .StartWith(Search)
                    .Select<string, Func<ModListMetadataVM, bool>>(txt =>
                    {
                        if (string.IsNullOrWhiteSpace(txt)) return _ => true;
                        return item => item.Metadata.Title.ContainsCaseInsensitive(txt) ||
                                       item.Metadata.Description.ContainsCaseInsensitive(txt) ||
                                       item.Metadata.Tags.Contains(txt);
                    });

                var onlyInstalledGamesFilter = this.ObservableForProperty(vm => vm.OnlyInstalled)
                    .Select(v => v.Value)
                    .Select<bool, Func<ModListMetadataVM, bool>>(onlyInstalled =>
                    {
                        if (onlyInstalled == false) return _ => true;
                        return item => _locator.IsInstalled(item.Metadata.Game);
                    })
                    .StartWith(_ => true);

                var showUnofficial = this.ObservableForProperty(vm => vm.ShowUnofficialLists)
                    .Select(v => v.Value)
                    .StartWith(false)
                    .Select<bool, Func<ModListMetadataVM, bool>>(unoffical =>
                    {
                        if (unoffical) return x => true;
                        return x => x.Metadata.Official;
                    });

                var showNSFWFilter = this.ObservableForProperty(vm => vm.ShowNSFW)
                    .Select(v => v.Value)
                    .Select<bool, Func<ModListMetadataVM, bool>>(showNsfw => { return item => item.Metadata.NSFW == showNsfw; })
                    .StartWith(item => item.Metadata.NSFW == false);

                var gameFilter = this.ObservableForProperty(vm => vm.GameType)
                    .Select(v => v.Value)
                    .Select<string, Func<ModListMetadataVM, bool>>(selected =>
                    {
                        _filteringOnGame = true;
                        if (selected is null or ALL_GAME_IDENTIFIER) return _ => true;
                        return item => item.Metadata.Game.MetaData().HumanFriendlyGameName == selected;
                    })
                    .StartWith(_ => true);

                var minSizeFilter = this.ObservableForProperty(vm => vm.MinSizeFilter)
                                     .Select(v => v.Value)
                                     .Select<double, Func<ModListMetadataVM, bool>>(minSize =>
                                     {
                                         return item =>
                                         {
                                             return item.Metadata.DownloadMetadata.TotalSize >= minSize;
                                         };
                                     });

                var maxSizeFilter = this.ObservableForProperty(vm => vm.MaxSizeFilter)
                                     .Select(v => v.Value)
                                     .Select<double, Func<ModListMetadataVM, bool>>(maxSize =>
                                     {
                                         return item =>
                                         {
                                             return item.Metadata.DownloadMetadata.TotalSize <= maxSize;
                                         };
                                     });
                                    

                var searchSorter = this.WhenValueChanged(vm => vm.Search)
                                        .Throttle(searchThrottle, RxApp.MainThreadScheduler)
                                        .Select(s => SortExpressionComparer<ModListMetadataVM>
                                                     .Descending(m => m.Metadata.Title.StartsWith(s, StringComparison.InvariantCultureIgnoreCase))
                                                     .ThenByDescending(m => m.Metadata.Title.Contains(s, StringComparison.InvariantCultureIgnoreCase))
                                                     .ThenByDescending(m => !m.IsBroken));
                _modLists.Connect()
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Filter(searchTextPredicates)
                    .Filter(onlyInstalledGamesFilter)
                    .Filter(showUnofficial)
                    .Filter(showNSFWFilter)
                    .Filter(gameFilter)
                    .Filter(minSizeFilter)
                    .Filter(maxSizeFilter)
                    .Sort(searchSorter)
                    .TreatMovesAsRemoveAdd()
                    .Bind(out _filteredModLists)
                    .Subscribe((_) =>
                    {
                        if (!_filteringOnGame)
                        {
                            var previousGameType = GameType;
                            SelectedGameTypeEntry = null;
                            GameTypeEntries = new(GetGameTypeEntries());
                            var nextEntry = GameTypeEntries.FirstOrDefault(gte => previousGameType == gte.GameIdentifier);
                            SelectedGameTypeEntry = nextEntry != default ? nextEntry : GameTypeEntries.FirstOrDefault(gte => GameType == ALL_GAME_IDENTIFIER);
                        }

                        _filteringOnGame = false;
                    })
                    .DisposeWith(disposables);
            });
        }

        private class FilterSettings
        {
            public string GameType { get; set; }
            public bool ShowNSFW { get; set; }
            public bool ShowUnofficialLists { get; set; }
            public bool OnlyInstalled { get; set; }
            public string Search { get; set; }
        }

        public override void Unload()
        {
            Error = null;
        }

        private async Task SaveSettings()
        {
            await _settingsManager.Save("modlist_gallery", new FilterSettings
            {
                GameType = GameType,
                ShowNSFW = ShowNSFW,
                ShowUnofficialLists = ShowUnofficialLists,
                Search = Search,
                OnlyInstalled = OnlyInstalled,
            });
        }

        private async Task LoadSettings()
        {
            using var ll = LoadingLock.WithLoading();
            RxApp.MainThreadScheduler.Schedule(await _settingsManager.Load<FilterSettings>("modlist_gallery"),
                (_, s) =>
            {
                SelectedGameTypeEntry = GameTypeEntries?.FirstOrDefault(gte => gte.GameIdentifier.Equals(s.GameType));
                ShowNSFW = s.ShowNSFW;
                ShowUnofficialLists = s.ShowUnofficialLists;
                Search = s.Search;
                OnlyInstalled = s.OnlyInstalled;
                return Disposable.Empty;
            });
        }

        private async Task LoadModLists()
        {
            using var ll = LoadingLock.WithLoading();
            try
            {
                var modLists = await _wjClient.LoadLists();
                _modLists.Edit(e =>
                {
                    e.Clear();
                    e.AddOrUpdate(modLists.Select(m =>
                        new ModListMetadataVM(_logger, this, m, _maintainer, _wjClient, _cancellationToken)));
                });
                MinSizeModlist = _modLists.Items.Any() ? _modLists.Items.MinBy(ml => ml.Metadata.DownloadMetadata.TotalSize) : null;
                MaxSizeModlist = _modLists.Items.Any() ? _modLists.Items.MaxBy(ml => ml.Metadata.DownloadMetadata.TotalSize) : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "While loading lists");
                ll.Fail();
            }
            ll.Succeed();
        }

        private List<GameTypeEntry> GetGameTypeEntries()
        {
            return ModLists.Select(fm => fm.Metadata)
                   .GroupBy(m => m.Game)
                   .Select(g => new GameTypeEntry(g.Key.MetaData(), g.Count()))
                   .OrderBy(gte => gte.GameMetaData.HumanFriendlyGameName)
                   .Prepend(GameTypeEntry.GetAllGamesEntry(ModLists.Count))
                   .ToList();
        }
    }
}