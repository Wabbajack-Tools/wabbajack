using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CG.Web.MegaApiClient;
using Microsoft.Extensions.Logging;
using Wabbajack.Downloaders.Interfaces;
using Wabbajack.DTOs;
using Wabbajack.DTOs.DownloadStates;
using Wabbajack.DTOs.Validation;
using Wabbajack.Hashing.xxHash64;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.RateLimiter;

namespace Wabbajack.Downloaders.ModDB;

public class MegaDownloader : ADownloader<Mega>, IUrlDownloader
{
    private const string MegaPrefix = "https://mega.nz/#!";
    private const string MegaFilePrefix = "https://mega.nz/file/";
    private readonly MegaApiClient _apiClient;
    private readonly ILogger<MegaDownloader> _logger;

    public MegaDownloader(ILogger<MegaDownloader> logger, MegaApiClient apiClient)
    {
        _logger = logger;
        _apiClient = apiClient;
    }

    public override async Task<bool> Prepare()
    {
        if (!_apiClient.IsLoggedIn)
            await _apiClient.LoginAsync();
        return true;
    }

    public override bool IsAllowed(ServerAllowList allowList, IDownloadState state)
    {
        var megaState = (Mega) state;
        return allowList.AllowedPrefixes.Any(p => megaState.Url.ToString().StartsWith(p));
    }

    public override IDownloadState? Resolve(IReadOnlyDictionary<string, string> iniData)
    {
        return iniData.ContainsKey("directURL") ? GetDownloaderState(iniData["directURL"]) : null;
    }

    public override Priority Priority => Priority.Normal;

    public IDownloadState? Parse(Uri uri)
    {
        return GetDownloaderState(uri.ToString());
    }

    public Uri UnParse(IDownloadState state)
    {
        return ((Mega) state).Url;
    }

    public override async Task<Hash> Download(Archive archive, Mega state, AbsolutePath destination, IJob job,
        CancellationToken token)
    {
        if (!_apiClient.IsLoggedIn)
            await _apiClient.LoginAsync();

        await using var ous = destination.Open(FileMode.Create, FileAccess.Write, FileShare.None);
        await using var ins = await _apiClient.DownloadAsync(state.Url, cancellationToken: token);
        return await ins.HashingCopy(ous, token, job);
    }

    private Mega? GetDownloaderState(string? url)
    {
        if (url == null) return null;

        if (url.StartsWith(MegaPrefix) || url.StartsWith(MegaFilePrefix))
            return new Mega {Url = new Uri(url)};
        return null;
    }

    public override async Task<bool> Verify(Archive archive, Mega archiveState, IJob job, CancellationToken token)
    {
        if (!_apiClient.IsLoggedIn)
            await _apiClient.LoginAsync();

        for (var times = 0; times < 5; times++)
        {
            try
            {
                var node = await _apiClient.GetNodeFromLinkAsync(archiveState.Url);
                if (node != null)
                    return true;
            }
            catch (Exception)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        }

        return false;
    }

    public override IEnumerable<string> MetaIni(Archive a, Mega state)
    {
        return new[] {$"directURL={state.Url}"};
    }
}