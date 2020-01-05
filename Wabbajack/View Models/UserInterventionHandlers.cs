﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ReactiveUI;
using Wabbajack.Common;
using Wabbajack.Lib.Downloaders;
using Wabbajack.Lib.NexusApi;
using Wabbajack.Lib.WebAutomation;

namespace Wabbajack
{
    public class UserInterventionHandlers
    {
        public MainWindowVM MainWindow { get; }

        public UserInterventionHandlers(MainWindowVM mvm)
        {
            MainWindow = mvm;
        }

        private async Task WrapBrowserJob(IUserIntervention intervention, Func<WebBrowserVM, CancellationTokenSource, Task> toDo)
        {
            CancellationTokenSource cancel = new CancellationTokenSource();
            var vm = await WebBrowserVM.GetNew();
            MainWindow.NavigateTo(vm);
            vm.BackCommand = ReactiveCommand.Create(() =>
            {
                cancel.Cancel();
                MainWindow.NavigateBack();
                intervention.Cancel();
            });

            try
            {
                await toDo(vm, cancel);
            }
            catch (TaskCanceledException)
            {
                intervention.Cancel();
            }
            catch (Exception ex)
            {
                Utils.Error(ex);
                intervention.Cancel();
            }

            MainWindow.NavigateBack();
        }

        public async Task Handle(IUserIntervention msg)
        {
            switch (msg)
            {
                case RequestNexusAuthorization c:
                    await WrapBrowserJob(msg, async (vm, cancel) =>
                    {
                        await vm.Driver.WaitForInitialized();
                        var key = await NexusApiClient.SetupNexusLogin(new CefSharpWrapper(vm.Browser), m => vm.Instructions = m, cancel.Token);
                        c.Resume(key);
                    });
                    break;
                case RequestLoversLabLogin c:
                    await WrapBrowserJob(msg, async (vm, cancel) =>
                    {
                        await vm.Driver.WaitForInitialized();
                        var data = await LoversLabDownloader.GetAndCacheLoversLabCookies(new CefSharpWrapper(vm.Browser), m => vm.Instructions = m, cancel.Token);
                        c.Resume(data);
                    });
                    break;
                case ConfirmationIntervention c:
                    break;
                default:
                    throw new NotImplementedException($"No handler for {msg}");
            }
        }


    }
}
