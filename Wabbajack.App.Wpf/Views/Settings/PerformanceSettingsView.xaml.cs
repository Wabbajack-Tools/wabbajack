﻿using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using ReactiveUI;
using Wabbajack.Paths.IO;

namespace Wabbajack;

/// <summary>
/// Interaction logic for PerformanceSettingsView.xaml
/// </summary>
public partial class PerformanceSettingsView : ReactiveUserControl<PerformanceSettingsVM>
{
    public PerformanceSettingsView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            ViewModel.WhenAnyValue(vm => vm.Settings)
                     .BindToStrict(this, v => v.SettingsControl.ItemsSource)
                     .DisposeWith(disposables);

            ViewModel.WhenAnyValue(vm => vm.MaxThreads)
                     .Select(mt => mt.ToString())
                     .BindToStrict(this, v => v.MaxThreadsText.Text)
                     .DisposeWith(disposables);

        });
    }

    private void TextBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        e.Handled = !new Regex("^[0-9]*$").IsMatch(e.Text);
    }
}
