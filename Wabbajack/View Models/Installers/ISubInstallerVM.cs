﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ReactiveUI;
using Wabbajack.Common;
using Wabbajack.Lib;

namespace Wabbajack
{
    public interface ISubInstallerVM
    {
        InstallerVM Parent { get; }
        AInstaller ActiveInstallation { get; }
        void Unload();
        bool SupportsAfterInstallNavigation { get; }
        void AfterInstallNavigation();
        int ConfigVisualVerticalOffset { get; }
        IObservable<bool> CanInstall { get; }
        Task Install();
    }
}
