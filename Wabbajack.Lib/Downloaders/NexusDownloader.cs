﻿using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Ceras;
using ReactiveUI;
using Wabbajack.Common;
using Wabbajack.Common.StatusFeed.Errors;
using Wabbajack.Lib.NexusApi;
using Wabbajack.Lib.Validation;
using Game = Wabbajack.Common.Game;

namespace Wabbajack.Lib.Downloaders
{
    public class NexusDownloader : IDownloader, INeedsLogin
    {
        private bool _prepared;
        private AsyncLock _lock = new AsyncLock();
        private UserStatus _status;
        private NexusApiClient _client;

        public IObservable<bool> IsLoggedIn => Utils.HaveEncryptedJsonObservable("nexusapikey");

        public string SiteName => "Nexus Mods";

        public IObservable<string> MetaInfo => Observable.Return("");

        public Uri SiteURL => new Uri("https://www.nexusmods.com");

        public Uri IconUri => new Uri("https://www.nexusmods.com/favicon.ico");

        public ReactiveCommand<Unit, Unit> TriggerLogin { get; }
        public ReactiveCommand<Unit, Unit> ClearLogin { get; }

        public NexusDownloader()
        {
            if (CLIArguments.ApiKey != null)
            {
                CLIArguments.ApiKey.ToEcryptedJson("nexusapikey");
            }

            TriggerLogin = ReactiveCommand.CreateFromTask(
                execute: () => Utils.CatchAndLog(NexusApiClient.RequestAndCacheAPIKey), 
                canExecute: IsLoggedIn.Select(b => !b).ObserveOnGuiThread());
            ClearLogin = ReactiveCommand.Create(
                execute: () => Utils.CatchAndLog(() => Utils.DeleteEncryptedJson("nexusapikey")),
                canExecute: IsLoggedIn.ObserveOnGuiThread());
        }

        public async Task<AbstractDownloadState> GetDownloaderState(dynamic archiveINI)
        {
            var general = archiveINI?.General;

            if (general.modID != null && general.fileID != null && general.gameName != null)
            {
                var name = (string)general.gameName;
                var gameMeta = GameRegistry.GetByMO2ArchiveName(name);
                var game = gameMeta != null ? GameRegistry.GetByMO2ArchiveName(name).Game : GameRegistry.GetByNexusName(name).Game;
                var client = await NexusApiClient.Get();
                dynamic info;
                try
                {
                    info = await client.GetModInfo(game, general.modID);
                }
                catch (Exception)
                {
                    Utils.Error($"Error getting mod info for Nexus mod with {general.modID}");
                    throw;
                }

                return new State
                {
                    Name = NexusApiUtils.FixupSummary(info.name),
                    Author = NexusApiUtils.FixupSummary(info.author),
                    Version = general.version ?? "0.0.0.0",
                    ImageURL = info.picture_url,
                    IsNSFW = info.contains_adult_content,
                    Description = NexusApiUtils.FixupSummary(info.summary),
                    GameName = general.gameName,
                    ModID = general.modID,
                    FileID = general.fileID
                };
            }

            return null;
        }

        public async Task Prepare()
        {
            if (!_prepared)
            {
                using var _ = await _lock.Wait();
                // Could have become prepared while we waited for the lock
                if (!_prepared)
                {
                    _client = await NexusApiClient.Get();
                    _status = await _client.GetUserStatus();
                    if (!_client.IsAuthenticated)
                    {
                        Utils.ErrorThrow(new UnconvertedError(
                            $"Authenticating for the Nexus failed. A nexus account is required to automatically download mods."));
                        return;
                    }
                    

                    if (!await _client.IsPremium())
                    {
                        var result = await Utils.Log(new YesNoIntervention(
                            "Wabbajack can operate without a premium account, but downloads will be slower and the install process will require more user interactions (you will have to start each download by hand). Are you sure you wish to continue?",
                            "Continue without Premium?")).Task;
                        if (result == ConfirmationIntervention.Choice.Abort)
                        {
                            Utils.ErrorThrow(new UnconvertedError($"Aborting at the request of the user"));
                        }
                    }
                    _prepared = true;
                }
            }
        }

        public class State : AbstractDownloadState, IMetaState
        {
            public string URL => $"http://nexusmods.com/{NexusApiUtils.ConvertGameName(GameName)}/mods/{ModID}";

            public string Name { get; set; }

            public string Author { get; set; }

            public string Version { get; set; }
            
            public string ImageURL { get; set; }
            
            public bool IsNSFW { get; set; }

            public string Description { get; set; }

            public async Task<bool> LoadMetaData()
            {
                return true;
            }

            public string GameName { get; set; }
            public string ModID { get; set; }
            public string FileID { get; set; }

            public override object[] PrimaryKey { get => new object[]{GameName, ModID, FileID};}

            public override bool IsWhitelisted(ServerWhitelist whitelist)
            {
                // Nexus files are always whitelisted
                return true;
            }

            public override async Task<bool> Download(Archive a, string destination)
            {
                string url;
                try
                {
                    var client = await NexusApiClient.Get();
                    url = await client.GetNexusDownloadLink(this);
                }
                catch (Exception ex)
                {
                    Utils.Log($"{a.Name} - Error getting Nexus download URL - {ex.Message}");
                    return false;
                }

                Utils.Log($"Downloading Nexus Archive - {a.Name} - {GameName} - {ModID} - {FileID}");

                return await new HTTPDownloader.State
                {
                    Url = url
                }.Download(a, destination);
            }

            public override async Task<bool> Verify(Archive a)
            {
                try
                {
                    var gameMeta = GameRegistry.GetByMO2ArchiveName(GameName) ?? GameRegistry.GetByNexusName(GameName);
                    if (gameMeta == null)
                        return false;

                    var game = gameMeta.Game;
                    if (!int.TryParse(ModID, out var modID))
                        return false;

                    var client = await NexusApiClient.Get();
                    var modFiles = await client.GetModFiles(game, modID);

                    if (!ulong.TryParse(FileID, out var fileID))
                        return false;

                    var found = modFiles.files
                        .FirstOrDefault(file => file.file_id == fileID && file.category_name != null);
                    return found != null;
                }
                catch (Exception ex)
                {
                    Utils.Log($"{Name} - {GameName} - {ModID} - {FileID} - Error getting Nexus download URL - {ex}");
                    return false;
                }

            }

            public override IDownloader GetDownloader()
            {
                return DownloadDispatcher.GetInstance<NexusDownloader>();
            }

            public override string GetManifestURL(Archive a)
            {
                return $"http://nexusmods.com/{NexusApiUtils.ConvertGameName(GameName)}/mods/{ModID}";
            }

            public override string[] GetMetaIni()
            {
                return new[] {"[General]", $"gameName={GameName}", $"modID={ModID}", $"fileID={FileID}"};
            }
        }
    }
}
