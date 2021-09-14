﻿using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Wabbajack.Common;
using Wabbajack.Common.IO;
using Wabbajack.Common.Serialization.Json;
using Wabbajack.Lib.Validation;

namespace Wabbajack.Lib.Downloaders
{
    public class ManualDownloader : IDownloader
    {
        class FileEvent
        {
            public string FullPath { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public long Size { get; set; }
        }

        public ManualDownloader()
        {
        }

        public async Task<AbstractDownloadState?> GetDownloaderState(dynamic archiveINI, bool quickMode)
        {
            var url = archiveINI?.General?.manualURL;
            return url != null ? new State(url) : null;
        }

        public Task Prepare() => Task.CompletedTask;

        [JsonName("ManualDownloader")]
        public class State : AbstractDownloadState
        {
            public string Url { get; }

            [JsonIgnore]
            public override object[] PrimaryKey => new object[] { Url };

            public State(string url)
            {
                Url = url;
            }

            public override bool IsWhitelisted(ServerWhitelist whitelist)
            {
                return Url == "<TESTING>" || whitelist.AllowedPrefixes.Any(p => Url.StartsWith(p));
            }

            public override async Task<bool> Download(Archive a, AbsolutePath destination)
            {
                var (uri, client) = await Utils.Log(await ManuallyDownloadFile.Create(this)).Task;
                var state = new HTTPDownloader.State(uri.ToString()) { Client = client };
                return await state.Download(a, destination);
            }

            public override async Task<bool> Verify(Archive a, CancellationToken? token)
            {
                return true;
            }

            public override IDownloader GetDownloader()
            {
                return DownloadDispatcher.GetInstance<ManualDownloader>();
            }

            public override string GetManifestURL(Archive a)
            {
                return Url;
            }

            public override string[] GetMetaIni()
            {
                return new []
                {
                    "[General]",
                    $"manualURL={Url}",
                };
            }
        }
    }
}
