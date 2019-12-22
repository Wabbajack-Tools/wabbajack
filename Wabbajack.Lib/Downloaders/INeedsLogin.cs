﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Wabbajack.Lib.Downloaders
{
    public interface INeedsLogin : INotifyPropertyChanged
    {
        ICommand TriggerLogin { get; }
        ICommand ClearLogin { get; }
        IObservable<bool> IsLoggedIn { get; }
        string SiteName { get; }
        string MetaInfo { get; }
        Uri SiteURL { get; }
        Uri IconUri { get; }

    }
}
