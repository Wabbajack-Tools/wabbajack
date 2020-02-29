﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Alphaleonis.Win32.Filesystem;
using Wabbajack.Common;
using Wabbajack.Lib.Downloaders.UrlDownloaders;

namespace Wabbajack.Lib.Downloaders
{
    public static class DownloadDispatcher
    {
        public static readonly List<IDownloader> Downloaders = new List<IDownloader>()
        {
            new GameFileSourceDownloader(),
            new MegaDownloader(),
            new DropboxDownloader(),
            new GoogleDriveDownloader(),
            new ModDBDownloader(),
            new NexusDownloader(),
            new MediaFireDownloader(),
            new LoversLabDownloader(),
            new VectorPlexusDownloader(),
            new DeadlyStreamDownloader(),
            new BethesdaNetDownloader(),
            new AFKModsDownloader(),
            new TESAllianceDownloader(),
            new YouTubeDownloader(),
            new HTTPDownloader(),
            new ManualDownloader(),
        };

        public static readonly List<IUrlInferencer> Inferencers = new List<IUrlInferencer>()
        {
            new BethesdaNetInferencer()
        };

        private static readonly Dictionary<Type, IDownloader> IndexedDownloaders;

        static DownloadDispatcher()
        {
            IndexedDownloaders = Downloaders.ToDictionary(d => d.GetType());
        }

        public static AbstractDownloadState Infer(Uri uri)
        {
            return Inferencers.Select(infer => infer.Infer(uri)).FirstOrDefault(result => result != null);
        }

        public static T GetInstance<T>() where T : IDownloader
        {
            var inst = (T)IndexedDownloaders[typeof(T)];
            inst.Prepare();
            return inst;
        }

        public static async Task<AbstractDownloadState> ResolveArchive(dynamic ini)
        {
            var states = await Task.WhenAll(Downloaders.Select(d => (Task<AbstractDownloadState>)d.GetDownloaderState(ini)));
            return states.FirstOrDefault(result => result != null);
        }

        /// <summary>
        /// Reduced version of Resolve archive that requires less information, but only works
        /// with a single URL string
        /// </summary>
        /// <param name="ini"></param>
        /// <returns></returns>
        public static AbstractDownloadState ResolveArchive(string url)
        {
            return Downloaders.OfType<IUrlDownloader>().Select(d => d.GetDownloaderState(url)).FirstOrDefault(result => result != null);
        }

        public static void PrepareAll(IEnumerable<AbstractDownloadState> states)
        {
            states.Select(s => s.GetDownloader().GetType())
                  .Distinct()
                  .Do(t => Downloaders.First(d => d.GetType() == t).Prepare());
        }

        public static async Task<bool> DownloadWithPossibleUpgrade(Archive archive, string destination)
        {
            var success = await Download(archive, destination);
            if (success)
            {
                await destination.FileHashCachedAsync();
                return true;
            }

            Utils.Log($"Download failed, looking for upgrade");
            var upgrade = await ClientAPI.GetModUpgrade(archive.Hash);
            if (upgrade == null)
            {
                Utils.Log($"No upgrade found for {archive.Hash}");
                return false;
            }

            Utils.Log($"Upgrading {archive.Hash}");
            var upgradePath = Path.Combine(Path.GetDirectoryName(destination), "_Upgrade_" + archive.Name);
            var upgradeResult = await Download(upgrade, upgradePath);
            if (!upgradeResult) return false;

            var patchName = $"{archive.Hash.FromBase64().ToHex()}_{upgrade.Hash.FromBase64().ToHex()}";
            var patchPath = Path.Combine(Path.GetDirectoryName(destination), "_Patch_" + patchName);

            var patchState = new Archive
            {
                Name = patchName,
                State = new HTTPDownloader.State
                {
                    Url = $"https://wabbajackcdn.b-cdn.net/updates/{patchName}"
                }
            };

            var patchResult = await Download(patchState, patchPath);
            if (!patchResult) return false;

            Utils.Status($"Applying Upgrade to {archive.Hash}");
            await using (var patchStream = File.OpenRead(patchPath))
            await using (var srcStream = File.OpenRead(upgradePath))
            await using (var destStream = File.Create(destination))
            {
                OctoDiff.Apply(srcStream, patchStream, destStream);
            }

            await destination.FileHashCachedAsync();

            return true;
        }

        private static async Task<bool> Download(Archive archive, string destination)
        {
            try
            {
                var result =  await archive.State.Download(archive, destination);
                if (!result) return false;

                if (archive.Hash == null) return true;
                var hash = await destination.FileHashCachedAsync();
                if (hash == archive.Hash) return true;

                Utils.Log($"Hashed download is incorrect");
                return false;

            }
            catch (Exception ex)
            {
                return false;
            }
        }
    }
}
