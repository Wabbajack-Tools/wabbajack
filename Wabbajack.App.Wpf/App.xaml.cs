﻿using System;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Wpf;
using NLog.Extensions.Logging;
using NLog.Targets;
using Orc.FileAssociation;
using ReactiveUI;
using Wabbajack.CLI.Builder;
using Wabbajack.DTOs;
using Wabbajack.DTOs.Interventions;
using Wabbajack.Interventions;
using Wabbajack.LoginManagers;
using Wabbajack.Models;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.Services.OSIntegrated;
using Wabbajack.UserIntervention;
using Wabbajack.Util;
using Ext = Wabbajack.Common.Ext;

namespace Wabbajack;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App
{
    private IHost _host;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        if (IsAdmin())
        {
            var messageBox = MessageBox.Show("Don't run Wabbajack as Admin!", "Error", MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK, MessageBoxOptions.DefaultDesktopOnly);
            if (messageBox == MessageBoxResult.OK)
            {
                Environment.Exit(1);
            }
            else
            {
                Environment.Exit(1);
            }
        }

        RxApp.MainThreadScheduler = new DispatcherScheduler(Dispatcher.CurrentDispatcher);
        _host = Host.CreateDefaultBuilder(Array.Empty<string>())
            .ConfigureLogging(AddLogging)
            .ConfigureServices((host, services) =>
            {
                ConfigureServices(services);
            })
            .Build();

        var webview2 = _host.Services.GetRequiredService<WebView2>();
        var currentDir = (AbsolutePath)Directory.GetCurrentDirectory();
        var webViewDir = currentDir.Combine("WebView2");
        if(webViewDir.DirectoryExists())
        {
            var logger = _host.Services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("Local WebView2 executable folder found. Using folder {0} instead of system binaries!", currentDir.Combine("WebView2"));
            webview2.CreationProperties = new CoreWebView2CreationProperties() { BrowserExecutableFolder = currentDir.Combine("WebView2").ToString() };
        }

        var args = e.Args;

        RxApp.MainThreadScheduler.Schedule(0, (_, _) =>
        {
            if (args.Length == 1)
            {
                var arg = args[0].ToAbsolutePath();
                if (arg.FileExists() && arg.Extension == Ext.Wabbajack)
                {
                    var mainWindow = _host.Services.GetRequiredService<MainWindow>();
                    mainWindow!.Show();
                    return Disposable.Empty;
                }
            } else if (args.Length > 0)
            {
                var builder = _host.Services.GetRequiredService<CommandLineBuilder>();
                builder.Run(e.Args).ContinueWith(async x =>
                {
                    Environment.Exit(await x);
                });
                return Disposable.Empty;
            }
            else
            {
                var mainWindow = _host.Services.GetRequiredService<MainWindow>();
                mainWindow!.Show();
                return Disposable.Empty;
            }

            return Disposable.Empty;
        });
    }

    private static bool IsAdmin()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;

        try
        {
            var identity = WindowsIdentity.GetCurrent();
            var owner = identity.Owner;
            if (owner is not null) return owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);

            var principle = new WindowsPrincipal(identity);
            return principle.IsInRole(WindowsBuiltInRole.Administrator);

        }
        catch (Exception)
        {
            return false;
        }
    }

    private void AddLogging(ILoggingBuilder loggingBuilder)
    {
        var config = new NLog.Config.LoggingConfiguration();

        var logFolder = KnownFolders.LauncherAwarePath.Combine("logs");
        if (!logFolder.DirectoryExists())
            logFolder.CreateDirectory();

        var fileTarget = new FileTarget("file")
        {
            FileName = logFolder.Combine("Wabbajack.current.log").ToString(),
            ArchiveFileName = logFolder.Combine("Wabbajack.{##}.log").ToString(),
            ArchiveOldFileOnStartup = true,
            MaxArchiveFiles = 10,
            Layout = "${processtime} [${level:uppercase=true}] (${logger}) ${message:withexception=true}",
            Header = "############ Wabbajack log file - ${longdate} ############"
        };

        var consoleTarget = new ConsoleTarget("console");

        var uiTarget = new LogStream
        {
            Name = "ui",
            Layout = "${message:withexception=false}",
        };

        loggingBuilder.Services.AddSingleton(uiTarget);

        config.AddRuleForAllLevels(fileTarget);
        config.AddRuleForAllLevels(consoleTarget);
        config.AddRuleForAllLevels(uiTarget);

        loggingBuilder.ClearProviders();
        loggingBuilder.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
        loggingBuilder.SetMinimumLevel(LogLevel.Information);
        loggingBuilder.AddNLog(config);
    }

    private static IServiceCollection ConfigureServices(IServiceCollection services)
    {
        services.AddOSIntegrated();

        // Orc.FileAssociation
        services.AddSingleton<IApplicationRegistrationService>(new ApplicationRegistrationService());

        // Singletons
        services.AddSingleton<CefService>();
        services.AddSingleton<IUserInterventionHandler, UserInterventionHandler>();
        services.AddSingleton<ImageCacheManager>();
        services.AddSingleton<SystemParametersConstructor>();
        services.AddSingleton<LauncherUpdater>();
        services.AddSingleton<ResourceMonitor>();

        var currentDir = (AbsolutePath)Directory.GetCurrentDirectory();
        var webViewDir = currentDir.Combine("webview2");
        services.AddSingleton<WebView2>();
        services.AddSingleton<BrowserWindow>();
        
        // ViewModels
        services.AddTransient<MainWindow>();
        services.AddTransient<MainWindowVM>();
        services.AddTransient<NavigationVM>();
        services.AddTransient<HomeVM>();
        services.AddTransient<ModListGalleryVM>();
        services.AddTransient<CompilerHomeVM>();
        services.AddTransient<CompilerDetailsVM>();
        services.AddTransient<CompilerFileManagerVM>();
        services.AddTransient<CompilerMainVM>();
        services.AddTransient<InstallationVM>();
        services.AddTransient<SettingsVM>();
        services.AddTransient<WebBrowserVM>();
        services.AddTransient<InfoVM>();
        services.AddTransient<ModListDetailsVM>();

        // Login Handlers
        services.AddTransient<VectorPlexusLoginHandler>();
        services.AddTransient<NexusLoginHandler>();
        services.AddTransient<LoversLabLoginHandler>();

        // Login Managers

        //Disabled LL because it is currently not used and broken due to the way LL butchers their API
        //services.AddAllSingleton<INeedsLogin, LoversLabLoginManager>();
        services.AddAllSingleton<INeedsLogin, NexusLoginManager>();
        //Disabled VP due to frequent login issues & because the only file that really got downloaded there has a mirror
        //services.AddAllSingleton<INeedsLogin, VectorPlexusLoginManager>();
        services.AddSingleton<ManualDownloadHandler>();
        services.AddSingleton<ManualBlobDownloadHandler>();

        // Verbs
        services.AddSingleton<CommandLineBuilder>();
        services.AddCLIVerbs();

        return services;
    }
}
