﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Wabbajack.Common;

namespace Wabbajack
{
    internal class AppState : INotifyPropertyChanged
    {
        private ICommand _begin;

        private ICommand _changeDownloadPath;

        private ICommand _changePath;
        private string _downloadLocation;

        private string _htmlReport;

        private bool _ignoreMissingFiles;
        private string _location;

        private string _mo2Folder;


        private string _mode;
        private ModList _modList;
        private string _modListName;

        private int _queueProgress;

        private ICommand _showReportCommand;
        private readonly DateTime _startTime;

        public volatile bool Dirty;

        private readonly Dispatcher dispatcher;

        public AppState(Dispatcher d, string mode)
        {
            // Ensure WJ is not being run in a download directory.
            if (Assembly.GetEntryAssembly().Location.ToLower().Contains("\\downloads\\"))
            {
                MessageBox.Show(
                    "This app seems to be running inside a folder called `Downloads`, such folders are often highly monitored by Antivirus software and they can often " +
                    "conflict with the operations Wabbajack needs to perform. Please move this executable outside of your `Downloads` folder and then restart the app.",
                    "Cannot run inside `Downloads`",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Environment.Exit(1);
            }

            // Check if LOOT is installed.
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string lootAppDataPath = $"{appDataPath}\\LOOT";
            if (!Directory.Exists(lootAppDataPath))
            {
                MessageBox.Show(
                   $"Wabbajack could not locate the LOOT application data at {lootAppDataPath}. " +
                    "Please make sure that LOOT is installed correctly before running Wabbajack.",
                    "LOOT not found",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Environment.Exit(1);
            }

            _startTime = DateTime.Now;
            LogFile = Assembly.GetExecutingAssembly().Location + ".log";

            if (LogFile.FileExists())
                File.Delete(LogFile);

            Mode = mode;
            Dirty = false;
            dispatcher = d;
            Log = new ObservableCollection<string>();
            Status = new ObservableCollection<CPUStatus>();
            InternalStatus = new List<CPUStatus>();

            var th = new Thread(() => UpdateLoop());
            th.Priority = ThreadPriority.BelowNormal;
            th.IsBackground = true;
            th.Start();
        }

        public ObservableCollection<string> Log { get; }
        public ObservableCollection<CPUStatus> Status { get; }

        public string Mode
        {
            get => _mode;
            set
            {
                _mode = value;
                OnPropertyChanged("Mode");
            }
        }

        public bool IgnoreMissingFiles
        {
            get => _ignoreMissingFiles;
            set
            {
                if (value)
                {
                    if (MessageBox.Show(
                            "Setting this value could result in broken installations. \n Are you sure you want to continue?",
                            "Ignore Missing Files?", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                        == MessageBoxResult.OK)
                        _ignoreMissingFiles = value;
                }
                else
                {
                    _ignoreMissingFiles = value;
                }

                OnPropertyChanged("IgnoreMissingFiles");
            }
        }

        public string ModListName
        {
            get => _modListName;
            set
            {
                _modListName = value;
                OnPropertyChanged("ModListName");
            }
        }

        public string Location
        {
            get => _location;
            set
            {
                _location = value;
                OnPropertyChanged("Location");
            }
        }

        public string DownloadLocation
        {
            get => _downloadLocation;
            set
            {
                _downloadLocation = value;
                OnPropertyChanged("DownloadLocation");
            }
        }

        public Visibility ShowReportButton => _htmlReport == null ? Visibility.Collapsed : Visibility.Visible;

        public string HTMLReport
        {
            get => _htmlReport;
            set
            {
                _htmlReport = value;
                OnPropertyChanged("HTMLReport");
                OnPropertyChanged("ShowReportButton");
            }
        }

        public int QueueProgress
        {
            get => _queueProgress;
            set
            {
                if (value != _queueProgress)
                {
                    _queueProgress = value;
                    OnPropertyChanged("QueueProgress");
                }
            }
        }


        private List<CPUStatus> InternalStatus { get; }
        public string LogFile { get; }

        public ICommand ChangePath
        {
            get
            {
                if (_changePath == null) _changePath = new LambdaCommand(() => true, () => ExecuteChangePath());
                return _changePath;
            }
        }

        public ICommand ChangeDownloadPath
        {
            get
            {
                if (_changeDownloadPath == null)
                    _changeDownloadPath = new LambdaCommand(() => true, () => ExecuteChangeDownloadPath());
                return _changeDownloadPath;
            }
        }

        public ICommand Begin
        {
            get
            {
                if (_begin == null) _begin = new LambdaCommand(() => true, () => ExecuteBegin());
                return _begin;
            }
        }

        public ICommand ShowReportCommand
        {
            get
            {
                if (_showReportCommand == null) _showReportCommand = new LambdaCommand(() => true, () => ShowReport());
                return _showReportCommand;
            }
        }

        private bool _uiReady = false;
        public bool UIReady
        {
            get => _uiReady;
            set
            {
                _uiReady = value;
                OnPropertyChanged("UIReady");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private void UpdateLoop()
        {
            while (true)
            {
                if (Dirty)
                    lock (InternalStatus)
                    {
                        var data = InternalStatus.ToArray();
                        dispatcher.Invoke(() =>
                        {
                            for (var idx = 0; idx < data.Length; idx += 1)
                                if (idx >= Status.Count)
                                    Status.Add(data[idx]);
                                else if (Status[idx] != data[idx])
                                    Status[idx] = data[idx];
                        });
                        Dirty = false;
                    }

                Thread.Sleep(1000);
            }
        }

        internal void ConfigureForInstall(ModList modlist)
        {
            _modList = modlist;
            Mode = "Installing";
            ModListName = _modList.Name;
            HTMLReport = _modList.ReportHTML;
            Location = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        }

        public void LogMsg(string msg)
        {
            msg = $"{(DateTime.Now - _startTime).TotalSeconds:0.##} - {msg}";
            dispatcher.Invoke(() => Log.Add(msg));
            lock (dispatcher)
            {
                File.AppendAllText(LogFile, msg + "\r\n");
            }
        }

        public void SetProgress(int id, string msg, int progress)
        {
            lock (InternalStatus)
            {
                Dirty = true;
                while (id >= InternalStatus.Count) InternalStatus.Add(new CPUStatus());

                InternalStatus[id] = new CPUStatus {ID = id, Msg = msg, Progress = progress};
            }
        }

        public void SetQueueSize(int max, int current)
        {
            if (max == 0)
                max = 1;
            var total = current * 100 / max;
            QueueProgress = total;
        }

        private void ExecuteChangePath()
        {
            if (Mode == "Installing")
            {
                var folder = UIUtils.ShowFolderSelectionDialog("Select Installation directory");
                if (folder != null)
                {
                    Location = folder;
                    if (_downloadLocation == null)
                        DownloadLocation = Path.Combine(Location, "downloads");
                }
            }
            else
            {
                var folder = UIUtils.ShowFolderSelectionDialog("Select Your MO2 profile directory");

                if (folder != null)
                {
                    var file = Path.Combine(folder, "modlist.txt");
                    if (!File.Exists(file))
                    {
                        Utils.Log($"No modlist.txt found at {file}");
                    }

                    Location = file;
                    ConfigureForBuild();
                }
            }
        }

        private void ExecuteChangeDownloadPath()
        {
            var folder = UIUtils.ShowFolderSelectionDialog("Select a location for MO2 downloads");
            if (folder != null) DownloadLocation = folder;
        }

        private void ConfigureForBuild()
        {
            var profile_folder = Path.GetDirectoryName(Location);
            var mo2folder = Path.GetDirectoryName(Path.GetDirectoryName(profile_folder));
            if (!File.Exists(Path.Combine(mo2folder, "ModOrganizer.exe")))
                LogMsg($"Error! No ModOrganizer2.exe found in {mo2folder}");

            var profile_name = Path.GetFileName(profile_folder);
            ModListName = profile_name;
            Mode = "Building";

            var tmp_compiler = new Compiler(mo2folder, Utils.Log);
            DownloadLocation = tmp_compiler.MO2DownloadsFolder;

            _mo2Folder = mo2folder;
        }

        private void ShowReport()
        {
            var file = Path.GetTempFileName() + ".html";
            File.WriteAllText(file, HTMLReport);
            Process.Start(file);
        }


        private void ExecuteBegin()
        {
            UIReady = false;
            if (Mode == "Installing")
            {
                var installer = new Installer(_modList, Location, msg => LogMsg(msg));
                installer.IgnoreMissingFiles = IgnoreMissingFiles;
                installer.DownloadFolder = DownloadLocation;
                var th = new Thread(() =>
                {
                    UIReady = false;
                    try
                    {
                        installer.Install();
                    }
                    catch (Exception ex)
                    {
                        while (ex.InnerException != null) ex = ex.InnerException;
                        LogMsg(ex.StackTrace);
                        LogMsg(ex.ToString());
                        LogMsg($"{ex.Message} - Can't continue");
                    }
                    finally
                    {
                        UIReady = true;
                    }
                });
                th.Priority = ThreadPriority.BelowNormal;
                th.Start();
            }
            else
            {
                var compiler = new Compiler(_mo2Folder, msg => LogMsg(msg));
                compiler.IgnoreMissingFiles = IgnoreMissingFiles;
                compiler.MO2Profile = ModListName;
                var th = new Thread(() =>
                {
                    UIReady = false;
                    try
                    {
                        compiler.Compile();
                        if (compiler.ModList != null && compiler.ModList.ReportHTML != null)
                            HTMLReport = compiler.ModList.ReportHTML;
                    }
                    catch (Exception ex)
                    {
                        while (ex.InnerException != null) ex = ex.InnerException;
                        LogMsg(ex.StackTrace);
                        LogMsg(ex.ToString());
                        LogMsg($"{ex.Message} - Can't continue");
                    }
                    finally
                    {
                        UIReady = true;
                    }
                });
                th.Priority = ThreadPriority.BelowNormal;
                th.Start();
            }
        }

        public class CPUStatus
        {
            public int Progress { get; internal set; }
            public string Msg { get; internal set; }
            public int ID { get; internal set; }
        }
    }
}