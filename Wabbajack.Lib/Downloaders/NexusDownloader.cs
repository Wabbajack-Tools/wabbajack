﻿using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ReactiveUI;
using Wabbajack.Common;
using Wabbajack.Common.StatusFeed.Errors;
using Wabbajack.Lib.NexusApi;
using Wabbajack.Lib.Validation;

namespace Wabbajack.Lib.Downloaders
{
    public class NexusDownloader : ViewModel, IDownloader, INeedsLogin
    {
        private bool _prepared;
        private SemaphoreSlim _lock = new SemaphoreSlim(1);
        private UserStatus _status;
        private NexusApiClient _client;

        public NexusDownloader()
        {
            TriggerLogin = ReactiveCommand.Create(async () => await NexusApiClient.RequestAndCacheAPIKey(), IsLoggedIn.Select(b => !b).ObserveOn(RxApp.MainThreadScheduler));
            ClearLogin = ReactiveCommand.Create(() => Utils.DeleteEncryptedJson("nexusapikey"), IsLoggedIn.ObserveOn(RxApp.MainThreadScheduler));
        }

        public IObservable<bool> IsLoggedIn => Utils.HaveEncryptedJsonObservable("nexusapikey");

        public string SiteName => "Nexus Mods";

        public string MetaInfo
        {
            get
            {
                return "";
            }
        } 


        public Uri SiteURL => new Uri("https://www.nexusmods.com");

        public Uri IconUri => new Uri("https://www.nexusmods.com/favicon.ico");
        
        public ICommand TriggerLogin { get; }
        public ICommand ClearLogin { get; }

        public async Task<AbstractDownloadState> GetDownloaderState(dynamic archiveINI)
        {
            var general = archiveINI?.General;

            if (general.modID != null && general.fileID != null && general.gameName != null)
            {
                var name = (string)general.gameName;
                var gameMeta = GameRegistry.GetByMO2ArchiveName(name);
                var game = gameMeta != null ? GameRegistry.GetByMO2ArchiveName(name).Game : GameRegistry.GetByNexusName(name).Game;
                var client = await NexusApiClient.Get();
                var info = await client.GetModInfo(game, general.modID);
                return new State
                {
                    GameName = general.gameName,
                    FileID = general.fileID,
                    ModID = general.modID,
                    Version = general.version ?? "0.0.0.0",
                    Author = info.author,
                    UploadedBy = info.uploaded_by,
                    UploaderProfile = info.uploaded_users_profile_url,
                    ModName = info.name,
                    SlideShowPic = info.picture_url,
                    NexusURL = NexusApiUtils.GetModURL(game, info.mod_id),
                    Summary = info.summary,
                    Adult = info.contains_adult_content

                };
            }

            return null;
        }

        public async Task Prepare()
        {
            if (!_prepared)
            {
                await _lock.WaitAsync();
                try
                {
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
                    }
                }
                finally
                {
                    _lock.Release();
                }
            }

            _prepared = true;

            if (_status.is_premium) return;
            Utils.ErrorThrow(new UnconvertedError($"Automated installs with Wabbajack requires a premium nexus account. {await _client.Username()} is not a premium account."));
        }

        public class State : AbstractDownloadState
        {
            public string Author;
            public string FileID;
            public string GameName;
            public string ModID;
            public string UploadedBy;
            public string UploaderProfile;
            public string Version;
            public string SlideShowPic;
            public string ModName;
            public string NexusURL;
            public string Summary;
            public bool Adult;

            public override bool IsWhitelisted(ServerWhitelist whitelist)
            {
                // Nexus files are always whitelisted
                return true;
            }

            public override async Task Download(Archive a, string destination)
            {
                string url;
                try
                {
                    var client = await NexusApiClient.Get();
                    url = await client.GetNexusDownloadLink(this, false);
                }
                catch (Exception ex)
                {
                    Utils.Log($"{a.Name} - Error Getting Nexus Download URL - {ex.Message}");
                    return;
                }

                Utils.Log($"Downloading Nexus Archive - {a.Name} - {GameName} - {ModID} - {FileID}");

                await new HTTPDownloader.State
                {
                    Url = url
                }.Download(a, destination);

            }

            public override async Task<bool> Verify()
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
                    Utils.Log($"{ModName} - {GameName} - {ModID} - {FileID} - Error Getting Nexus Download URL - {ex}");
                    return false;
                }

            }

            public override IDownloader GetDownloader()
            {
                return DownloadDispatcher.GetInstance<NexusDownloader>();
            }

            public override string GetReportEntry(Archive a)
            {
                var profile = UploaderProfile.Replace("/games/",
                    "/" + NexusApiUtils.ConvertGameName(GameName).ToLower() + "/");

                return string.Join("\n", 
                    $"* [{a.Name}](http://nexusmods.com/{NexusApiUtils.ConvertGameName(GameName)}/mods/{ModID})", 
                    $"    * Author : [{UploadedBy}]({profile})", 
                    $"    * Version : {Version}");
            }
        }
    }
}
