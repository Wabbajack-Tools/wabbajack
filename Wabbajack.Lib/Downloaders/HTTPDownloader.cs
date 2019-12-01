﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using Ceras;
using Wabbajack.Common;
using Wabbajack.Lib.Validation;
using File = Alphaleonis.Win32.Filesystem.File;

namespace Wabbajack.Lib.Downloaders
{
    public class HTTPDownloader : IDownloader, IUrlDownloader
    {

        public AbstractDownloadState GetDownloaderState(dynamic archiveINI)
        {
            var url = archiveINI?.General?.directURL;
            return GetDownloaderState(url, archiveINI);

        }

        public AbstractDownloadState GetDownloaderState(string uri)
        {
            return GetDownloaderState(uri, null);
        }

        public AbstractDownloadState GetDownloaderState(string url, dynamic archiveINI)
        {
            if (url != null)
            {
                var tmp = new State
                {
                    Url = url
                };
                if (archiveINI?.General?.directURLHeaders != null)
                {
                    tmp.Headers = new List<string>();
                    tmp.Headers.AddRange(archiveINI?.General.directURLHeaders.Split('|'));
                }
                return tmp;
            }

            return null;
        }

        public void Prepare()
        {
        }

        [MemberConfig(TargetMember.All)]
        public class State : AbstractDownloadState
        {
            public string Url { get; set; }

            public List<string> Headers { get; set; }

            [Exclude]
            public HttpClient Client { get; set; }

            public override bool IsWhitelisted(ServerWhitelist whitelist)
            {
                return whitelist.AllowedPrefixes.Any(p => Url.StartsWith(p));
            }

            public override void Download(Archive a, string destination)
            {
                DoDownload(a, destination, true);
            }

            public bool DoDownload(Archive a, string destination, bool download)
            {
                var client = Client ?? new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", Consts.UserAgent);

                if (Headers != null)
                    foreach (var header in Headers)
                    {
                        var idx = header.IndexOf(':');
                        var k = header.Substring(0, idx);
                        var v = header.Substring(idx + 1);
                        client.DefaultRequestHeaders.Add(k, v);
                    }

                long totalRead = 0;
                var bufferSize = 1024 * 32;

                var response = client.GetSync(Url);
                var stream = response.Content.ReadAsStreamAsync();
                try
                {
                    stream.Wait();
                }
                catch (Exception)
                {
                }

                if (stream.IsFaulted || response.StatusCode != HttpStatusCode.OK)
                {
                    Utils.Log($"While downloading {Url} - {stream.Exception.ExceptionToString()}");
                    return false;
                }

                if (!download)
                    return true;

                var headerVar = a.Size == 0 ? "1" : a.Size.ToString();
                if (response.Content.Headers.Contains("Content-Length"))
                    headerVar = response.Content.Headers.GetValues("Content-Length").FirstOrDefault();

                var contentSize = headerVar != null ? long.Parse(headerVar) : 1;

                Directory.CreateDirectory(Path.GetDirectoryName(destination));

                using (var webs = stream.Result)
                using (var fs = File.OpenWrite(destination))
                {
                    var buffer = new byte[bufferSize];
                    while (true)
                    {
                        var read = webs.Read(buffer, 0, bufferSize);
                        if (read == 0) break;
                        Utils.Status($"Downloading {a.Name}", (int)(totalRead * 100 / contentSize));

                        fs.Write(buffer, 0, read);
                        totalRead += read;
                    }
                }

                return true;
            }

            public override bool Verify()
            {
                return DoDownload(new Archive {Name = ""}, "", false);
            }

            public override IDownloader GetDownloader()
            {
                return DownloadDispatcher.GetInstance<HTTPDownloader>();
            }

            public override string GetReportEntry(Archive a)
            {
                return $"* [{a.Name} - {Url}]({Url})";
            }
        }
    }
}
