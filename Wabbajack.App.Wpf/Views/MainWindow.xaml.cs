﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Input;
using DynamicData.Binding;
using MahApps.Metro.Controls;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Wabbajack.Common;
using Wabbajack.Messages;
using Wabbajack.Paths.IO;
using Wabbajack.Util;
using ReactiveMarbles.ObservableEvents;
using System.Reactive;
using System.Diagnostics;
using System.Linq;
using System.Collections;
using System.Windows.Data;
using System.Reactive.Concurrency;
using System.Windows.Controls;
using System.Windows.Markup;

namespace Wabbajack;

/// <summary>
///     Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : MetroWindow
{
    private MainWindowVM _mwvm;
    private readonly ILogger<MainWindow> _logger;
    private readonly SystemParametersConstructor _systemParams;
    private readonly Stopwatch _mousePressedTimer;

    public MainWindow(ILogger<MainWindow> logger, SystemParametersConstructor systemParams, LauncherUpdater updater, MainWindowVM vm)
    {
        InitializeComponent();
        _mwvm = vm;
        DataContext = vm;
        _logger = logger;
        _systemParams = systemParams;
        _mousePressedTimer = new Stopwatch();

        try
        {
            // Wire any unhandled crashing exceptions to log before exiting
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                // Don't do any special logging side effects
                _logger.LogError((Exception)e.ExceptionObject, "Uncaught error");
                Environment.Exit(-1);
            };

            Closed += (s, e) =>
            {
                _logger.LogInformation("Beginning shutdown...");
                _mwvm.CancelRunningTasks(TimeSpan.FromSeconds(10));
                
                // Cleaning the temp folder when the app closes since it can take up multiple Gigabytes of Storage
                var tempDirectory = Environment.CurrentDirectory + "\\temp";
                _logger.LogInformation("Clearing {TempDir}",tempDirectory);
                var directoryInfo = new DirectoryInfo(tempDirectory);
                try
                {
                    foreach (var file in directoryInfo.EnumerateFiles())
                    {
                        file.Delete();
                    }

                    foreach (var dir in directoryInfo.EnumerateDirectories())
                    {
                        dir.Delete(true);
                    }

                    _logger.LogInformation("Finished clearing {TempDir}", tempDirectory);
                }
                catch (DirectoryNotFoundException)
                {
                    _logger.LogInformation("Unable to find {TempDir}", tempDirectory);
                }
                
                Application.Current.Shutdown();
            };

            _logger.LogInformation("Wabbajack Build - {Sha}",ThisAssembly.Git.Sha);
            _logger.LogInformation("Running in {EntryPoint}", KnownFolders.EntryPoint);

            var p = _systemParams.Create();

            _logger.LogInformation("Detected Windows Version: {Version}", Environment.OSVersion.VersionString);

            _logger.LogInformation(
                "System settings - ({MemorySize} RAM) ({PageSize} Page), Display: {ScreenWidth} x {ScreenHeight} ({Vram} VRAM - VideoMemorySizeMb={ENBVRam})",
                p.SystemMemorySize.ToFileSizeString(), p.SystemPageSize.ToFileSizeString(), p.ScreenWidth, p.ScreenHeight, p.VideoMemorySize.ToFileSizeString(), p.EnbLEVRAMSize);

            try
            {
                var drives = DriveHelper.Drives;
                var partitions = DriveHelper.Partitions;
                foreach (var drive in drives)
                {
                    if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                    var driveType = partitions[drive.RootDirectory.Name[0]].MediaType.ToString();
                    var rootDir = drive.RootDirectory.ToString();
                    var freeSpace = drive.AvailableFreeSpace.ToFileSizeString();
                    _logger.LogInformation("Detected drive {RootDirectory} - Type: {DriveType}, Free Space: {FreeSpace}", rootDir, driveType, freeSpace);
                }
            }
            catch(Exception ex)
            {
                _logger.LogError("Failed to retrieve drive information. Exception {ex}", ex.ToString());
            }

            if (p.SystemPageSize == 0)
                _logger.LogInformation("Pagefile is disabled! This will cause issues such as crashing with Wabbajack and other applications!");

            var _ = updater.Run();

            // Bring window to the front if it isn't already
            this.Initialized += (s, e) =>
            {
                this.Activate();
                this.Topmost = true;
                this.Focus();
            };
            this.ContentRendered += (s, e) =>
            {
                this.Topmost = false;
            };

            vm.WhenAnyValue(vm => vm.ActivePane)
                .Subscribe(pane => WizardSteps.Visibility = (pane is IProgressVM) ? Visibility.Visible : Visibility.Collapsed);

            vm.WhenAnyValue(vm => vm.ActivePane)
              .Where(pane => pane is IProgressVM)
              .Subscribe(pane =>
              {
                  var wizardVM = (ProgressViewModel)pane;

                  wizardVM.WhenAnyValue(x => x.ConfigurationText)
                          .BindTo(this, view => view.ConfigurationText.Text)
                          .DisposeWith(wizardVM.CompositeDisposable);
                  wizardVM.WhenAnyValue(x => x.ProgressText)
                          .BindTo(this, view => view.ProgressText.Text)
                          .DisposeWith(wizardVM.CompositeDisposable);
                  wizardVM.WhenAnyValue(x => x.ProgressPercent.Value)
                          .Select(x => x.IsGreaterThan(0) && !x.IsOne() ? Visibility.Visible : Visibility.Hidden)
                          .BindTo(this, view => view.ProgressPercentage.Visibility)
                          .DisposeWith(wizardVM.CompositeDisposable);
                  wizardVM.WhenAnyValue(x => x.ProgressPercent.Value)
                          .Select(x => (int)(x * 100) + "%")
                          .BindTo(this, view => view.ProgressPercentage.Text)
                          .DisposeWith(wizardVM.CompositeDisposable);
                  wizardVM.WhenAnyValue(x => x.ProgressPercent.Value)
                          .BindTo(this, view => view.WizardProgress.Value)
                          .DisposeWith(wizardVM.CompositeDisposable);
                  wizardVM.WhenAnyValue(x => x.CurrentStep)
                          .ObserveOnGuiThread()
                          .Subscribe(step =>
                          {
                              ConfigurationText.Width = double.NaN;
                              ConfigurationText.HorizontalAlignment = HorizontalAlignment.Center;
                              ProgressText.Width = double.NaN;
                              ProgressText.HorizontalAlignment = HorizontalAlignment.Center;
                              if (step == Step.Configuration)
                              {
                                  ConfigurationText.Width = 500;
                                  ConfigurationText.HorizontalAlignment = HorizontalAlignment.Left;
                              }
                              else if (step == Step.Busy)
                              {
                                  ProgressText.Width = 500;
                                  ProgressText.HorizontalAlignment = HorizontalAlignment.Left;
                              }
                          })
                          .DisposeWith(wizardVM.CompositeDisposable);

              });

            vm.WhenAnyValue(vm => vm.ActivePane)
                .Subscribe(pane => InfoButton.Visibility = (pane is IHasInfoVM) ? Visibility.Visible : Visibility.Collapsed);
            vm.WhenAnyValue(vm => vm.InfoCommand)
                .BindTo(this, view => view.InfoButton.Command);

            vm.WhenAnyValue(vm => vm.MinimizeCommand)
                .BindTo(this, view => view.MinimizeButton.Command);

            vm.WhenAnyValue(vm => vm.MaximizeCommand)
                .BindTo(this, view => view.MaximizeButton.Command);

            vm.WhenAnyValue(vm => vm.CloseCommand)
                .BindTo(this, view => view.CloseButton.Command);

            vm.WhenAnyValue(vm => vm.NavigationVisible)
                .Subscribe(v => NavigationColumn.Width = v ? new GridLength(115, GridUnitType.Pixel) : new GridLength(0, GridUnitType.Pixel));

            TitleBar.Events().MouseDown
                .Subscribe(x => TitleBar_OnMouseDown(this, x));

            FloatingWindowBackground.Events().MouseDown
                .Subscribe(x => FloatingWindowBackground_OnMouseDown(this, x));

            vm.WhenAnyValue(vm => vm.ActiveFloatingPane)
                .Select(x => x == null ? Visibility.Hidden : Visibility.Visible)
                .BindTo(this, view => view.FloatingWindow.Visibility);

            this.Events().KeyDown
                .Subscribe(x => HandleKeyDown(this, x));

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "During Main Window Startup");
            Environment.Exit(-1);
        }

        vm.WhenAnyValue(vm => vm.WindowTitle)
          .BindToStrict(this, view => view.AppName.Text);
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _mwvm.ShutdownApplication().Wait();
    }

    private void TitleBar_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Application.Current.MainWindow.WindowState == WindowState.Maximized)
            Application.Current.MainWindow.WindowState = WindowState.Normal;

        DragMove();
    }

    private void FloatingWindowBackground_OnMouseDown(object sender, MouseButtonEventArgs x)
    {
        if (x.ButtonState == MouseButtonState.Pressed)
        {
            _mousePressedTimer.Restart();
            DragMove();
        }
        if(x.ButtonState == MouseButtonState.Released)
        {
            if(_mousePressedTimer.Elapsed < TimeSpan.FromSeconds(0.2))
            {
                ShowFloatingWindow.Send(FloatingScreenType.None);
            }
            _mousePressedTimer.Stop();
        }
    }

    private void HandleKeyDown(MainWindow mainWindow, KeyEventArgs x)
    {
        if(x.Key == Key.Escape)
        {
            if (((MainWindowVM)DataContext).ActiveFloatingPane != null)
                ShowFloatingWindow.Send(FloatingScreenType.None);
        }
    }

}
