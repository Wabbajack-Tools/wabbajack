﻿using System;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Wabbajack.Common;
using Wabbajack.Lib;

namespace Wabbajack
{
    public class InstallationConfigVM : ViewModel
    {
        private MainWindowVM _mainWindow;

        private readonly ObservableAsPropertyHelper<ViewModel> _configArea;
        public ViewModel ConfigArea => _configArea.Value;

        private readonly Lazy<MO2InstallerConfigVM> _mo2InstallerConfig;

        //.wabbajack file stuff
        public string WJFileFilter => $"*{ExtensionManager.Extension}|*{ExtensionManager.Extension}";

        [Reactive]
        public string WJFilePath { get; set; }

        private readonly ObservableAsPropertyHelper<IErrorResponse> _wjFileError;
        public IErrorResponse WJFileError => _wjFileError.Value;

        //ModList stuff
        private readonly ObservableAsPropertyHelper<ModListVM> _modList;
        public ModListVM ModList => _modList.Value;

        public InstallationConfigVM(MainWindowVM mainWindow)
        {
            _mainWindow = mainWindow;
            _mo2InstallerConfig = new Lazy<MO2InstallerConfigVM>(() =>new MO2InstallerConfigVM(this));

            _wjFileError = this.WhenAny(x => x.WJFilePath).Select(Utils.IsFilePathValid)
                .ToProperty(this, nameof(WJFileError));

            _modList = this.WhenAny(x => x.WJFilePath).Select(path =>
            {
                if (path == null) return default;
                var modList = Installer.LoadFromFile(path);
                return modList == null ? default : new ModListVM(modList, path);
            }).ToProperty(this, nameof(ModList));

            _configArea = this.WhenAny(x => x.ModList).Select<ModListVM, ViewModel>(modList =>
            {
                if (modList == null) return default;
                //switch (modList.ModManager)
                //{
                //    switch between MO2 and Vortex
                //}
                _mo2InstallerConfig.Value.Modlist = modList;
                return _mo2InstallerConfig.Value;
            }).ToProperty(this, nameof(ConfigArea));
        }

        public void Install()
        {
            _mainWindow.Page = 4;
        }
    }
}
