using System;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Wabbajack.Lib;

namespace Wabbajack
{
    public class MainWindowVM : ViewModel
    {
        public MainWindow MainWindow { get; }

        public MainSettings Settings { get; }

        private readonly ObservableAsPropertyHelper<ViewModel> _ContentArea;
        public ViewModel ContentArea => _ContentArea.Value;

        [Reactive]
        public int Page { get; set; } = 0;

        private readonly Lazy<StartupVM> _startupScreen;
        private readonly Lazy<ModListGalleryVM> _modlistGallery;

        public MainWindowVM(MainWindow mainWindow, MainSettings settings)
        {
            MainWindow = mainWindow;
            Settings = settings;
            _startupScreen = new Lazy<StartupVM>(()=>new StartupVM(this));
            _modlistGallery = new Lazy<ModListGalleryVM>(()=> new ModListGalleryVM(this));

            _ContentArea = this.WhenAny(x => x.Page)
                .Select<int, ViewModel>(m =>
                {
                    switch (m)
                    {
                        case 0: return _startupScreen.Value;
                        case 1: return _modlistGallery.Value;
                        default: return default;
                    }
                })
                .ToProperty(this, nameof(ContentArea));
        }
    }
}
