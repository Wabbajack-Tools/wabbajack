﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ReactiveUI;
using Wabbajack.Lib;

namespace Wabbajack
{
    public class SettingsVM : BackNavigatingVM
    {
        public MainWindowVM MWVM { get; }
        public LoginManagerVM Login { get; }
        public PerformanceSettings Performance { get; }
        public SlideShowSettings SlideShowSettings { get; }

        public AuthorFilesVM AuthorFile { get; }
        
        public SettingsVM(MainWindowVM mainWindowVM)
            : base(mainWindowVM)
        {
            MWVM = mainWindowVM;
            Login = new LoginManagerVM(this);
            Performance = mainWindowVM.Settings.Performance;
            AuthorFile = new AuthorFilesVM(this);
            SlideShowSettings = mainWindowVM.Settings.Installer.SlideShowSettings;
        }

    }
}
