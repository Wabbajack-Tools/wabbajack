﻿using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Wabbajack.Common;
using Wabbajack.Common.StatusFeed;
using Wabbajack.Lib;

namespace Wabbajack
{
    /// <summary>
    /// Main View Model for the application.
    /// Keeps track of which sub view is being shown in the window, and has some singleton wiring like WorkQueue and Logging.
    /// </summary>
    public class MainWindowVM : ViewModel
    {
        public MainWindow MainWindow { get; }

        public MainSettings Settings { get; }

        [Reactive]
        public ViewModel ActivePane { get; private set; }

        public ObservableCollectionExtended<IStatusMessage> Log { get; } = new ObservableCollectionExtended<IStatusMessage>();

        public readonly Lazy<CompilerVM> Compiler;
        public readonly Lazy<InstallerVM> Installer;
        public readonly Lazy<ModListGalleryVM> Gallery;
        public readonly ModeSelectionVM ModeSelectionVM;
        public readonly UserInterventionHandlers UserInterventionHandlers;
        public readonly LoginManagerVM LoginManagerVM;

        private readonly Stack<ViewModel> _navigationTrail = new Stack<ViewModel>();

        public ICommand CopyVersionCommand { get; }

        public ICommand ShowLoginManagerVM { get; }
        public ICommand GoBackCommand { get; }
        public string VersionDisplay { get; }

        public MainWindowVM(MainWindow mainWindow, MainSettings settings)
        {
            MainWindow = mainWindow;
            Settings = settings;
            Installer = new Lazy<InstallerVM>(() => new InstallerVM(this));
            Compiler = new Lazy<CompilerVM>(() => new CompilerVM(this));
            Gallery = new Lazy<ModListGalleryVM>(() => new ModListGalleryVM(this));
            ModeSelectionVM = new ModeSelectionVM(this);
            UserInterventionHandlers = new UserInterventionHandlers(this);
            LoginManagerVM = new LoginManagerVM(this);

            // Set up logging
            Utils.LogMessages
                .ObserveOn(RxApp.TaskpoolScheduler)
                .ToObservableChangeSet()
                .Buffer(TimeSpan.FromMilliseconds(250), RxApp.TaskpoolScheduler)
                .Where(l => l.Count > 0)
                .ObserveOn(RxApp.MainThreadScheduler)
                .FlattenBufferResult()
                .Bind(Log)
                .Subscribe()
                .DisposeWith(CompositeDisposable);

            Utils.LogMessages
                .OfType<IUserIntervention>()
                .ObserveOnGuiThread()
                .SelectTask(async msg =>
                {
                    try
                    {
                        await UserInterventionHandlers.Handle(msg);
                    }
                    catch (Exception ex) 
                    when (ex.GetType() != typeof(TaskCanceledException))
                    {
                        Utils.Error(ex, $"Error while handling user intervention of type {msg?.GetType()}");
                        try
                        {
                            if (!msg.Handled)
                            {
                                msg.Cancel();
                            }
                        }
                        catch (Exception cancelEx)
                        {
                            Utils.Error(cancelEx, $"Error while cancelling user intervention of type {msg?.GetType()}");
                        }
                    }
                })
                .Subscribe()
                .DisposeWith(CompositeDisposable);

            if (IsStartingFromModlist(out var path))
            {
                Installer.Value.ModListLocation.TargetPath = path;
                NavigateTo(Installer.Value);
            }
            else
            {
                // Start on mode selection
                NavigateTo(ModeSelectionVM);
            }

            try
            {
                System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
                FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(assembly.Location);
                VersionDisplay = $"v{fvi.FileVersion}";
            }
            catch (Exception ex)
            {
                Utils.Error(ex);
                VersionDisplay = "ERROR";
            }
            CopyVersionCommand = ReactiveCommand.Create(() =>
            {
                Clipboard.SetText($"Wabbajack {VersionDisplay}\n{ThisAssembly.Git.Sha}");
            });
        }
        private static bool IsStartingFromModlist(out string modlistPath)
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length != 3 || !args[1].Contains("-i"))
            {
                modlistPath = default;
                return false;
            }

            modlistPath = args[2];
            return true;
        }


        public void OpenInstaller(string path)
        {
            if (path == null) return;
            var installer = Installer.Value;
            Settings.Installer.LastInstalledListLocation = path;
            NavigateTo(installer);
            installer.ModListLocation.TargetPath = path;
        }

        public void NavigateBack()
        {
            if (_navigationTrail.Count == 0)
            {
                ActivePane = ModeSelectionVM;
            }
            ActivePane = _navigationTrail.Pop();
        }

        public void NavigateTo(ViewModel vm)
        {
            if (ActivePane != null)
            {
                _navigationTrail.Push(ActivePane);
            }
            ActivePane = vm;
        }

        public void ShutdownApplication()
        {
            Dispose();
            Settings.PosX = MainWindow.Left;
            Settings.PosY = MainWindow.Top;
            Settings.Width = MainWindow.Width;
            Settings.Height = MainWindow.Height;
            MainSettings.SaveSettings(Settings);
            Application.Current.Shutdown();
        }
    }
}
