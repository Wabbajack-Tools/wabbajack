using System.Reactive;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Wabbajack.App.ViewModels;

namespace Wabbajack.App.Controls;

public class RemovableItemViewModel : ViewModelBase
{
    public RemovableItemViewModel()
    {
        Activator = new ViewModelActivator();
    }

    [Reactive] public string Text { get; set; }

    [Reactive] public ReactiveCommand<Unit, Unit> DeleteCommand { get; set; }
}