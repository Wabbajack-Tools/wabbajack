﻿using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading.Tasks;
using DynamicData;
using DynamicData.Binding;
using Wabbajack.Common;
using Wabbajack.Lib;

namespace Wabbajack
{
    public class MO2CompilerVM : ViewModel, ISubCompilerVM
    {
        private readonly MO2CompilationSettings settings;

        private readonly ObservableAsPropertyHelper<string> _Mo2Folder;
        public string Mo2Folder => _Mo2Folder.Value;

        private readonly ObservableAsPropertyHelper<string> _MOProfile;
        public string MOProfile => _MOProfile.Value;

        public FilePickerVM DownloadLocation { get; }

        public FilePickerVM ModlistLocation { get; }

        public IReactiveCommand BeginCommand { get; }

        private readonly ObservableAsPropertyHelper<bool> _Compiling;
        public bool Compiling => _Compiling.Value;

        private readonly ObservableAsPropertyHelper<ModlistSettingsEditorVM> _ModlistSettings;
        public ModlistSettingsEditorVM ModlistSettings => _ModlistSettings.Value;

        [Reactive]
        public StatusUpdateTracker StatusTracker { get; private set; }

        public MO2CompilerVM(CompilerVM parent)
        {
            this.ModlistLocation = new FilePickerVM()
            {
                ExistCheckOption = FilePickerVM.ExistCheckOptions.On,
                PathType = FilePickerVM.PathTypeOptions.File,
                PromptTitle = "Select Modlist"
            };
            this.DownloadLocation = new FilePickerVM()
            {
                ExistCheckOption = FilePickerVM.ExistCheckOptions.On,
                PathType = FilePickerVM.PathTypeOptions.Folder,
                PromptTitle = "Select Download Location",
            };

            this._Mo2Folder = this.WhenAny(x => x.ModlistLocation.TargetPath)
                .Select(loc =>
                {
                    try
                    {
                        var profile_folder = Path.GetDirectoryName(loc);
                        return Path.GetDirectoryName(Path.GetDirectoryName(profile_folder));
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                })
                .ToProperty(this, nameof(this.Mo2Folder));
            this._MOProfile = this.WhenAny(x => x.ModlistLocation.TargetPath)
                .Select(loc =>
                {
                    try
                    {
                        var profile_folder = Path.GetDirectoryName(loc);
                        return Path.GetFileName(profile_folder);
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                })
                .ToProperty(this, nameof(this.MOProfile));

            // Wire missing Mo2Folder to signal error state for Modlist Location
            this.ModlistLocation.AdditionalError = this.WhenAny(x => x.Mo2Folder)
                .Select<string, IErrorResponse>(moFolder =>
                {
                    if (Directory.Exists(moFolder)) return ErrorResponse.Success;
                    return ErrorResponse.Fail($"MO2 Folder could not be located from the given modlist location.{Environment.NewLine}Make sure your modlist is inside a valid MO2 distribution.");
                });

            // Wire start command
            this.BeginCommand = ReactiveCommand.CreateFromTask(
                canExecute: Observable.CombineLatest(
                        this.WhenAny(x => x.ModlistLocation.InError),
                        this.WhenAny(x => x.DownloadLocation.InError),
                        resultSelector: (ml, down) => !ml && !down)
                    .ObserveOnGuiThread(),
                execute: async () =>
                {
                    MO2Compiler compiler;
                    try
                    {
                        compiler = new MO2Compiler(this.Mo2Folder)
                        {
                            MO2Profile = this.MOProfile,
                            ModListName = this.ModlistSettings.ModListName,
                            ModListAuthor = this.ModlistSettings.AuthorText,
                            ModListDescription = this.ModlistSettings.Description,
                            ModListImage = this.ModlistSettings.ImagePath.TargetPath,
                            ModListWebsite = this.ModlistSettings.Website,
                            ModListReadme = this.ModlistSettings.ReadMeText.TargetPath,
                        };
                        // TODO: USE RX HERE
                        compiler.TextStatus.Subscribe(Utils.Log);

                        // Compile progress updates and populate ObservableCollection
                        compiler.QueueStatus
                            .ObserveOn(RxApp.TaskpoolScheduler)
                            .ToObservableChangeSet(x => x.ID)
                            .Batch(TimeSpan.FromMilliseconds(250), RxApp.TaskpoolScheduler)
                            .EnsureUniqueChanges()
                            .ObserveOn(RxApp.MainThreadScheduler)
                            .Sort(SortExpressionComparer<CPUStatus>.Ascending(s => s.ID), SortOptimisations.ComparesImmutableValuesOnly)
                            .Bind(parent.MWVM.StatusList)
                            .Subscribe()
                            .DisposeWith(this.CompositeDisposable);

                        compiler.PercentCompleted.Subscribe(parent.MWVM._progressPercent);
                    }
                    catch (Exception ex)
                    {
                        while (ex.InnerException != null) ex = ex.InnerException;
                        Utils.Log($"Compiler error: {ex.ExceptionToString()}");
                        return;
                    }

                    try
                    {
                        await compiler.Begin();
                    }
                    catch (Exception ex)
                    {
                        while (ex.InnerException != null) ex = ex.InnerException;
                        Utils.Log($"Compiler error: {ex.ExceptionToString()}");
                    }
                    finally
                    {
                        this.StatusTracker = null;
                        compiler.Dispose();
                    }
                    
                });
            this._Compiling = this.BeginCommand.IsExecuting
                .ToProperty(this, nameof(this.Compiling));

            // Load settings
            this.settings = parent.MWVM.Settings.Compiler.MO2Compilation;
            this.ModlistLocation.TargetPath = settings.LastCompiledProfileLocation;
            if (!string.IsNullOrWhiteSpace(settings.DownloadLocation))
            {
                this.DownloadLocation.TargetPath = settings.DownloadLocation;
            }
            parent.MWVM.Settings.SaveSignal
                .Subscribe(_ => Unload())
                .DisposeWith(this.CompositeDisposable);

            // Load custom modlist settings per MO2 profile
            this._ModlistSettings = Observable.CombineLatest(
                    this.WhenAny(x => x.ModlistLocation.ErrorState),
                    this.WhenAny(x => x.ModlistLocation.TargetPath),
                    resultSelector: (State, Path) => (State, Path))
                // A short throttle is a quick hack to make the above changes "atomic"
                .Throttle(TimeSpan.FromMilliseconds(25))
                .Select(u =>
                {
                    if (u.State.Failed) return null;
                    var modlistSettings = settings.ModlistSettings.TryCreate(u.Path);
                    return new ModlistSettingsEditorVM(modlistSettings)
                    {
                        ModListName = this.MOProfile
                    };
                })
                // Interject and save old while loading new
                .Pairwise()
                .Do(pair =>
                {
                    pair.Previous?.Save();
                    pair.Current?.Init();
                })
                .Select(x => x.Current)
                // Save to property
                .ObserveOnGuiThread()
                .ToProperty(this, nameof(this.ModlistSettings));

            // If Mo2 folder changes and download location is empty, set it for convenience
            this.WhenAny(x => x.Mo2Folder)
                .DelayInitial(TimeSpan.FromMilliseconds(100))
                .Where(x => Directory.Exists(x))
                .FilterSwitch(
                    this.WhenAny(x => x.DownloadLocation.Exists)
                        .Invert())
                .Subscribe(x =>
                {
                    try
                    {
                        var tmp_compiler = new MO2Compiler(this.Mo2Folder);
                        this.DownloadLocation.TargetPath = tmp_compiler.MO2DownloadsFolder;
                    }
                    catch (Exception ex)
                    {
                        Utils.Log($"Error setting default download location {ex}");
                    }
                })
                .DisposeWith(this.CompositeDisposable);
        }

        public void Unload()
        {
            settings.DownloadLocation = this.DownloadLocation.TargetPath;
            settings.LastCompiledProfileLocation = this.ModlistLocation.TargetPath;
            this.ModlistSettings?.Save();
        }
    }
}
