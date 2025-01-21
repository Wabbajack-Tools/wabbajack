﻿using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows;
using ReactiveUI;

namespace Wabbajack;

/// <summary>
/// Interaction logic for ModListTileView.xaml
/// </summary>
public partial class ModListTileView : ReactiveUserControl<BaseModListMetadataVM>
{
    public ModListTileView()
    {
        InitializeComponent();
        this.WhenActivated(disposables =>
        {
            ViewModel.WhenAnyValue(vm => vm.Image)
                     .BindToStrict(this, v => v.ModlistImage.ImageSource)
                     .DisposeWith(disposables);

            var textXformed = ViewModel.WhenAnyValue(vm => vm.Metadata.Title)
                .CombineLatest(ViewModel.WhenAnyValue(vm => vm.Metadata.ImageContainsTitle),
                            ViewModel.WhenAnyValue(vm => vm.IsBroken))
                .Select(x => x.Second && !x.Third ? "" : x.First);

            ViewModel.WhenAnyValue(x => x.LoadingImageLock.IsLoading)
                .Select(x => x ? Visibility.Visible : Visibility.Collapsed)
                .BindToStrict(this, x => x.LoadingProgress.Visibility)
                .DisposeWith(disposables);

            ViewModel.WhenAnyValue(x => x.IsBroken)
                .Select(x => x ? Visibility.Visible : Visibility.Collapsed)
                .BindToStrict(this, view => view.Overlay.Visibility)
                .DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.DetailsCommand, v => v.ModlistButton)
                .DisposeWith(disposables);
        });
    }
}
