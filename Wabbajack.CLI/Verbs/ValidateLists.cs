using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP;
using Microsoft.Extensions.Logging;
using Wabbajack.CLI.Services;
using Wabbajack.Common;
using Wabbajack.Compiler.PatchCache;
using Wabbajack.Downloaders;
using Wabbajack.Downloaders.Interfaces;
using Wabbajack.DTOs;
using Wabbajack.DTOs.DownloadStates;
using Wabbajack.DTOs.GitHub;
using Wabbajack.DTOs.JsonConverters;
using Wabbajack.DTOs.ModListValidation;
using Wabbajack.DTOs.ServerResponses;
using Wabbajack.DTOs.Validation;
using Wabbajack.Hashing.xxHash64;
using Wabbajack.Installer;
using Wabbajack.Networking.Discord;
using Wabbajack.Networking.GitHub;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.RateLimiter;
using Wabbajack.Server.Lib.DTOs;
using Wabbajack.Server.Lib.TokenProviders;

namespace Wabbajack.CLI.Verbs;

public class ValidateLists : IVerb
{
    private static readonly Uri MirrorPrefix = new("https://mirror.wabbajack.org");
    private readonly WriteOnlyClient _discord;
    private readonly DownloadDispatcher _dispatcher;
    private readonly DTOSerializer _dtos;
    private readonly IResource<IFtpSiteCredentials> _ftpRateLimiter;
    private readonly IFtpSiteCredentials _ftpSiteCredentials;
    private readonly Client _gitHubClient;

    private readonly ILogger<ValidateLists> _logger;
    private readonly ParallelOptions _parallelOptions;
    private readonly Random _random;
    private readonly TemporaryFileManager _temporaryFileManager;
    private readonly Networking.WabbajackClientApi.Client _wjClient;

    public ValidateLists(ILogger<ValidateLists> logger, Networking.WabbajackClientApi.Client wjClient,
        Client gitHubClient, TemporaryFileManager temporaryFileManager,
        DownloadDispatcher dispatcher, DTOSerializer dtos, ParallelOptions parallelOptions,
        IFtpSiteCredentials ftpSiteCredentials, IResource<IFtpSiteCredentials> ftpRateLimiter,
        WriteOnlyClient discordClient)
    {
        _logger = logger;
        _wjClient = wjClient;
        _gitHubClient = gitHubClient;
        _temporaryFileManager = temporaryFileManager;
        _dispatcher = dispatcher;
        _dtos = dtos;
        _parallelOptions = parallelOptions;
        _ftpSiteCredentials = ftpSiteCredentials;
        _ftpRateLimiter = ftpRateLimiter;
        _discord = discordClient;
        _random = new Random();
    }

    public Command MakeCommand()
    {
        var command = new Command("validate-lists");
        command.Add(new Option<List[]>(new[] {"-l", "-lists"}, "Lists of lists to validate") {IsRequired = true});
        command.Add(new Option<AbsolutePath>(new[] {"-r", "--reports"}, "Location to store validation report outputs"));
        command.Add(new Option<AbsolutePath>(new[] {"-a", "--archives"},
                "Location to store archives (files are named as the hex version of their hashes)")
            {IsRequired = true});

        command.Add(new Option<AbsolutePath>(new[] {"--other-archives"},
                "Look for files here before downloading (stored by hex hash name)")
            {IsRequired = false});

        command.Description = "Gets a list of modlists, validates them and exports a result list";
        command.Handler = CommandHandler.Create(Run);
        return command;
    }

    public async Task<int> Run(List[] lists, AbsolutePath archives, AbsolutePath reports, AbsolutePath otherArchives)
    {
        reports.CreateDirectory();
        var archiveManager = new ArchiveManager(_logger, archives);
        var token = CancellationToken.None;

        _logger.LogInformation("Scanning for existing patches/mirrors");
        var mirroredFiles = await AllMirroredFiles(token);
        _logger.LogInformation("Found {count} mirrored files", mirroredFiles.Count);
        var patchFiles = await AllPatchFiles(token);
        _logger.LogInformation("Found {count} patches", patchFiles.Count);

        var otherArchiveManager = otherArchives == default ? null : new ArchiveManager(_logger, otherArchives);

        _logger.LogInformation("Loading Mirror Allow List");
        var mirrorAllowList = await _wjClient.LoadMirrorAllowList();
        var upgradedArchives = await _wjClient.LoadUpgradedArchives();

        var validationCache = new LazyCache<string, Archive, (ArchiveStatus Status, Archive archive)>
        (x => x.State.PrimaryKeyString + x.Hash,
            archive => DownloadAndValidate(archive, archiveManager, otherArchiveManager, mirrorAllowList, token));

        var mirrorCache = new LazyCache<string, Archive, (ArchiveStatus Status, Archive archive)>
        (x => x.State.PrimaryKeyString + x.Hash,
            archive => AttemptToMirrorArchive(archive, archiveManager, mirrorAllowList, mirroredFiles, token));

        var patchCache = new LazyCache<string, Archive, (ArchiveStatus Status, ValidatedArchive? ValidatedArchive)>
        (x => x.State.PrimaryKeyString + x.Hash,
            archive => AttemptToPatchArchive(archive, archiveManager, upgradedArchives, patchFiles, token));

        var stopWatch = Stopwatch.StartNew();
        var listData = await lists.SelectAsync(async l => await _gitHubClient.GetData(l))
            .SelectMany(l => l.Lists)
            .ToArray();

        var validatedLists = await listData.PMapAll(async modList =>
        {
            var validatedList = new ValidatedModList
            {
                Name = modList.Title,
                ModListHash = modList.DownloadMetadata?.Hash ?? default,
                MachineURL = modList.Links.MachineURL,
                Version = modList.Version
            };

            if (modList.ForceDown)
            {
                _logger.LogWarning("List is ForceDown, skipping");
                validatedList.Status = ListStatus.ForcedDown;
                return validatedList;
            }

            using var scope = _logger.BeginScope("MachineURL: {machineURL}", modList.Links.MachineURL);
            _logger.LogInformation("Verifying {machineURL} - {title}", modList.Links.MachineURL, modList.Title);
            await DownloadModList(modList, archiveManager, CancellationToken.None);

            ModList modListData;
            try
            {
                _logger.LogInformation("Loading Modlist");
                modListData =
                    await StandardInstaller.LoadFromFile(_dtos,
                        archiveManager.GetPath(modList.DownloadMetadata!.Hash));
            }
            catch (JsonException ex)
            {
                validatedList.Status = ListStatus.ForcedDown;
                return validatedList;
            }

            _logger.LogInformation("Verifying {count} archives", modListData.Archives.Length);

            var archives = await modListData.Archives.PMapAll(async archive =>
            {
                //var result = await DownloadAndValidate(archive, archiveManager, token);
                var result = await validationCache.Get(archive);

                if (result.Status == ArchiveStatus.InValid)
                {
                    result = await mirrorCache.Get(archive);
                }

                if (result.Status == ArchiveStatus.InValid)
                {
                    _logger.LogInformation("Looking for patch");
                    var patchResult = await patchCache.Get(archive);
                    if (result.Status == ArchiveStatus.Updated)
                        return patchResult.ValidatedArchive;
                    return new ValidatedArchive
                    {
                        Original = archive,
                        Status = ArchiveStatus.InValid
                    };
                }

                return new ValidatedArchive
                {
                    Original = archive,
                    Status = result.Status,
                    PatchedFrom = result.Status is ArchiveStatus.Mirrored or ArchiveStatus.Updated
                        ? result.archive
                        : null
                };
            }).ToArray();

            validatedList.Archives = archives;
            validatedList.Status = archives.Any(a => a.Status == ArchiveStatus.InValid)
                ? ListStatus.Failed
                : ListStatus.Available;
            return validatedList;
        }).ToArray();

        var allArchives = validatedLists.SelectMany(l => l.Archives).ToList();
        _logger.LogInformation("Validated {count} lists in {elapsed}", validatedLists.Length, stopWatch.Elapsed);
        _logger.LogInformation(" - {count} Valid", allArchives.Count(a => a.Status is ArchiveStatus.Valid));
        _logger.LogInformation(" - {count} Invalid", allArchives.Count(a => a.Status is ArchiveStatus.InValid));
        _logger.LogInformation(" - {count} Mirrored", allArchives.Count(a => a.Status is ArchiveStatus.Mirrored));
        _logger.LogInformation(" - {count} Updated", allArchives.Count(a => a.Status is ArchiveStatus.Updated));

        foreach (var invalid in allArchives.Where(a => a.Status is ArchiveStatus.InValid)
            .DistinctBy(a => a.Original.Hash))
        {
            _logger.LogInformation("-- Invalid {Hash}: {PrimaryKeyString}", invalid.Original.Hash.ToHex(),
                invalid.Original.State.PrimaryKeyString);
        }

        await ExportReports(reports, validatedLists, token);

        return 0;
    }

    private async Task<(ArchiveStatus Status, ValidatedArchive? archive)> AttemptToPatchArchive(Archive archive,
        ArchiveManager archiveManager, Dictionary<Hash, ValidatedArchive> upgradedArchives,
        HashSet<(Hash, Hash)> existingPatches, CancellationToken token)
    {
        if (!archiveManager.HaveArchive(archive.Hash))
            return (ArchiveStatus.InValid, null);

        if (upgradedArchives.TryGetValue(archive.Hash, out var foundUpgrade))
        {
            if (await _dispatcher.Verify(foundUpgrade.PatchedFrom!, token))
            {
                return (ArchiveStatus.Updated, foundUpgrade);
            }
        }

        var upgrade = await _dispatcher.FindUpgrade(archive, _temporaryFileManager, token);
        if (upgrade == null)
            return (ArchiveStatus.InValid, null);

        var tempFile = _temporaryFileManager.CreateFile();
        await _dispatcher.Download(upgrade, tempFile.Path, token);
        if (!await _dispatcher.Verify(upgrade, token))
            return (ArchiveStatus.InValid, null);

        await archiveManager.Ingest(tempFile.Path, token);

        if (existingPatches.Contains((upgrade.Hash, archive.Hash)))
        {
            return (ArchiveStatus.Updated, new ValidatedArchive
            {
                Original = archive,
                Status = ArchiveStatus.Updated,
                PatchUrl = _wjClient.GetPatchUrl(upgrade.Hash, archive.Hash),
                PatchedFrom = upgrade
            });
        }

        await using var sigFile = _temporaryFileManager.CreateFile();
        await using var patchFile = _temporaryFileManager.CreateFile();
        await using var srcStream =
            archiveManager.GetPath(upgrade.Hash).Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var destStream =
            archiveManager.GetPath(archive.Hash).Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var sigStream = sigFile.Path.Open(FileMode.Create, FileAccess.ReadWrite);
        await using var patchOutput = patchFile.Path.Open(FileMode.Create, FileAccess.ReadWrite);
        OctoDiff.Create(destStream, srcStream, sigStream, patchOutput);
        await patchOutput.DisposeAsync();
        await UploadPatchToCDN(patchFile.Path, $"{upgrade.Hash.ToHex()}_{archive.Hash.ToHex()}", token);

        return (ArchiveStatus.Updated, new ValidatedArchive
        {
            Original = archive,
            Status = ArchiveStatus.Updated,
            PatchUrl = _wjClient.GetPatchUrl(upgrade.Hash, archive.Hash),
            PatchedFrom = upgrade
        });
    }

    private async Task UploadPatchToCDN(AbsolutePath patchFile, string patchName, CancellationToken token)
    {
        for (var times = 0; times < 5; times++)
        {
            try
            {
                _logger.Log(LogLevel.Information,
                    $"Uploading {patchFile.Size().ToFileSizeString()} patch file to CDN {patchName}");
                using var client = await (await _ftpSiteCredentials.Get())[StorageSpace.Patches].GetClient(_logger);

                await client.UploadFileAsync(patchFile.ToString(), patchName, FtpRemoteExists.Overwrite, token: token);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error uploading {patchFile} to CDN");
                if (ex.InnerException != null)
                    _logger.LogError(ex.InnerException, "Inner Exception");
            }
        }

        _logger.Log(LogLevel.Error, $"Couldn't upload {patchFile} to {patchName}");
    }


    private async Task ExportReports(AbsolutePath reports, ValidatedModList[] validatedLists, CancellationToken token)
    {
        foreach (var validatedList in validatedLists)
        {
            var baseFolder = reports.Combine(validatedList.MachineURL);
            baseFolder.CreateDirectory();
            await using var jsonFile = baseFolder.Combine("status").WithExtension(Ext.Json)
                .Open(FileMode.Create, FileAccess.Write, FileShare.None);
            await _dtos.Serialize(validatedList, jsonFile, true);

            await using var mdFile = baseFolder.Combine("status").WithExtension(Ext.Md)
                .Open(FileMode.Create, FileAccess.Write, FileShare.None);
            await using var sw = new StreamWriter(mdFile);

            await sw.WriteLineAsync($"## Validation Report - {validatedList.Name} ({validatedList.MachineURL})");
            await sw.WriteAsync("\n\n");

            async Task WriteSection(TextWriter w, ArchiveStatus status, string sectionName)
            {
                var archives = validatedList.Archives.Where(a => a.Status == status).ToArray();
                await w.WriteLineAsync($"### {sectionName} ({archives.Length})");

                foreach (var archive in archives.OrderBy(a => a.Original.Name))
                {
                    if (_dispatcher.TryGetDownloader(archive.Original, out var downloader) &&
                        downloader is IUrlDownloader u)
                    {
                        await w.WriteLineAsync(
                            $"*  [{archive.Original.Name}]({u.UnParse(archive.Original.State)})");
                    }
                    else
                    {
                        await w.WriteLineAsync(
                            $"*  {archive.Original.Name}");
                    }
                }
            }

            await WriteSection(sw, ArchiveStatus.InValid, "Invalid");
            await WriteSection(sw, ArchiveStatus.Updated, "Updated");
            await WriteSection(sw, ArchiveStatus.Mirrored, "Mirrored");
            await WriteSection(sw, ArchiveStatus.Valid, "Valid");


            try
            {
                var oldSummary = await _wjClient.GetDetailedStatus(validatedList.MachineURL);

                if (oldSummary.ModListHash != validatedList.ModListHash)
                {
                    await _discord.SendAsync(Channel.Ham,
                        $"Finished processing {validatedList.Name} ({validatedList.MachineURL}) v{validatedList.Version} ({oldSummary.ModListHash} -> {validatedList.ModListHash})",
                        token);
                }

                if (oldSummary.Failures != validatedList.Failures)
                {
                    if (validatedList.Failures == 0)
                    {
                        await _discord.SendAsync(Channel.Ham,
                            new DiscordMessage
                            {
                                Embeds = new[]
                                {
                                    new DiscordEmbed
                                    {
                                        Title =
                                            $"{validatedList.Name} (`{validatedList.MachineURL}`) is now passing.",
                                        Url = new Uri(
                                            $"https://github.com/wabbajack-tools/mod-lists/blob/master/reports/{validatedList.MachineURL}/status.md")
                                    }
                                }
                            }, token);
                    }
                    else
                    {
                        await _discord.SendAsync(Channel.Ham,
                            new DiscordMessage
                            {
                                Embeds = new[]
                                {
                                    new DiscordEmbed
                                    {
                                        Title =
                                            $"Number of failures in {validatedList.Name} (`{validatedList.MachineURL}`) was {oldSummary.Failures} is now {validatedList.Failures}",
                                        Url = new Uri(
                                            $"https://github.com/wabbajack-tools/mod-lists/blob/master/reports/{validatedList.MachineURL}/status.md")
                                    }
                                }
                            }, token);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "While sending discord message for {MachineURl}", validatedList.MachineURL);
            }
        }


        var summaries = validatedLists.Select(l => new ModListSummary
        {
            Failed = l.Archives.Count(f => f.Status == ArchiveStatus.InValid),
            Mirrored = l.Archives.Count(f => f.Status == ArchiveStatus.Mirrored),
            Passed = l.Archives.Count(f => f.Status == ArchiveStatus.Valid),
            MachineURL = l.MachineURL,
            Name = l.Name,
            Updating = 0
        }).ToArray();


        await using var summaryFile = reports.Combine("modListSummary.json")
            .Open(FileMode.Create, FileAccess.Write, FileShare.None);
        await _dtos.Serialize(summaries, summaryFile, true);


        var upgradedMetas = validatedLists.SelectMany(v => v.Archives)
            .Where(a => a.Status is ArchiveStatus.Mirrored or ArchiveStatus.Updated)
            .DistinctBy(a => a.Original.Hash)
            .OrderBy(a => a.Original.Hash)
            .ToArray();
        await using var upgradedMetasFile = reports.Combine("upgraded.json")
            .Open(FileMode.Create, FileAccess.Write, FileShare.None);
        await _dtos.Serialize(upgradedMetas, upgradedMetasFile, true);
    }

    private async Task<(ArchiveStatus Status, Archive archive)> AttemptToMirrorArchive(Archive archive,
        ArchiveManager archiveManager, ServerAllowList mirrorAllowList, HashSet<Hash> previouslyMirrored,
        CancellationToken token)
    {
        // Do we have a file to mirror?
        if (!archiveManager.HaveArchive(archive.Hash)) return (ArchiveStatus.InValid, archive);

        // Are we allowed to mirror the file?
        if (!_dispatcher.Matches(archive, mirrorAllowList)) return (ArchiveStatus.InValid, archive);

        var mirroredArchive = new Archive
        {
            Name = archive.Name,
            Size = archive.Size,
            Hash = archive.Hash,
            State = new WabbajackCDN
            {
                Url = new Uri($"{MirrorPrefix}{archive.Hash.ToHex()}")
            }
        };
        mirroredArchive.Meta = _dispatcher.MetaIniSection(mirroredArchive);

        // If it's already mirrored, we can exit
        if (previouslyMirrored.Contains(archive.Hash)) return (ArchiveStatus.Mirrored, mirroredArchive);

        // We need to mirror the file, but do we have a copy to mirror?
        if (!archiveManager.HaveArchive(archive.Hash)) return (ArchiveStatus.InValid, mirroredArchive);

        var srcPath = archiveManager.GetPath(archive.Hash);

        var definition = await _wjClient.GenerateFileDefinition(srcPath);

        using (var client = await GetMirrorFtpClient(token))
        {
            using var job = await _ftpRateLimiter.Begin("Starting uploading mirrored file", 0, token);
            await client.CreateDirectoryAsync($"{definition.Hash.ToHex()}", token);
            await client.CreateDirectoryAsync($"{definition.Hash.ToHex()}/parts", token);
        }

        string MakePath(long idx)
        {
            return $"{definition!.Hash.ToHex()}/parts/{idx}";
        }

        /* Outdated
        await definition.Parts.PDo(_parallelOptions, async part =>
        {
            _logger.LogInformation("Uploading mirror part of {name} {hash} ({index}/{length})", archive.Name, archive.Hash, part.Index, definition.Parts.Length);
            using var job = await _ftpRateLimiter.Begin("Uploading mirror part", part.Size, token);
            
            var buffer = new byte[part.Size];
            await using (var fs = srcPath.Open(FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Position = part.Offset;
                await fs.ReadAsync(buffer, token);
            }
            
            var tsk = job.Report((int)part.Size, token);
            await CircuitBreaker.WithAutoRetryAllAsync(_logger, async () =>{
                using var client = await GetMirrorFtpClient(token);
                var name = MakePath(part.Index);
                await client.UploadAsync(new MemoryStream(buffer), name, token: token);
            });
            await tsk;

        });

*/
        await CircuitBreaker.WithAutoRetryAllAsync(_logger, async () =>
        {
            using var client = await GetMirrorFtpClient(token);
            _logger.LogInformation($"Finishing mirror upload");
            using var job = await _ftpRateLimiter.Begin("Finishing mirror upload", 0, token);

            await using var ms = new MemoryStream();
            await using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true))
            {
                await _dtos.Serialize(definition, gz);
            }

            ms.Position = 0;
            var remoteName = $"{definition.Hash.ToHex()}/definition.json.gz";
            await client.UploadAsync(ms, remoteName, token: token);
        });


        return (ArchiveStatus.Mirrored, mirroredArchive);
    }

    private async Task<(ArchiveStatus, Archive)> DownloadAndValidate(Archive archive, ArchiveManager archiveManager,
        ArchiveManager? otherArchiveManager, ServerAllowList mirrorAllowList, CancellationToken token)
    {
        switch (archive.State)
        {
            case GameFileSource:
                return (ArchiveStatus.Valid, archive);
            case Manual:
                return (ArchiveStatus.Valid, archive);
            case TESAlliance:
                return (ArchiveStatus.Valid, archive);
        }

        bool ShouldDownload()
        {
            var downloader = _dispatcher.Downloader(archive);
            if (downloader == null ||
                (downloader is not IUrlDownloader && !_dispatcher.Matches(archive, mirrorAllowList)))
            {
                return true;
            }

            return false;
        }

        if (ShouldDownload() && !archiveManager.HaveArchive(archive.Hash) && archive.State is not Nexus or WabbajackCDN)
        {
            _logger.LogInformation("Downloading {name} {hash}", archive.Name, archive.Hash);

            if (otherArchiveManager != null && otherArchiveManager.HaveArchive(archive.Hash))
            {
                _logger.LogInformation("Found {name} {hash} in other archive manager", archive.Name, archive.Hash);
                await archiveManager.Ingest(otherArchiveManager.GetPath(archive.Hash), token);
            }
            else
            {
                try
                {
                    await using var tempFile = _temporaryFileManager.CreateFile();
                    var hash = await _dispatcher.Download(archive, tempFile.Path, token);
                    if (hash != archive.Hash)
                        return (ArchiveStatus.InValid, archive);
                    await archiveManager.Ingest(tempFile.Path, token);
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "Downloading {primaryKeyString}", archive.State.PrimaryKeyString);
                    return (ArchiveStatus.InValid, archive);
                }
            }
        }

        try
        {
            for (var attempts = 0; attempts < 3; attempts++)
            {
                var valid = await _dispatcher.Verify(archive, token);
                if (valid)
                    return (ArchiveStatus.Valid, archive);
                var delay = _random.Next(200, 1200);
                _logger.LogWarning(
                    "Archive {primaryKeyString} is invalid retrying in {Delay} ms ({Attempt} of {MaxAttempts})",
                    archive.State.PrimaryKeyString, delay, attempts, 3);
                await Task.Delay(delay, token);
            }

            _logger.LogWarning("Archive {primaryKeyString} is invalid", archive.State.PrimaryKeyString);
            return (ArchiveStatus.InValid, archive);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "While verifying {primaryKeyString}", archive.State.PrimaryKeyString);
            return (ArchiveStatus.InValid, archive);
        }
    }

    private async Task<Hash> DownloadModList(ModlistMetadata modList, ArchiveManager archiveManager,
        CancellationToken token)
    {
        if (archiveManager.HaveArchive(modList.DownloadMetadata!.Hash))
        {
            _logger.LogInformation("Previously downloaded {hash} not re-downloading", modList.Links.MachineURL);
            return modList.DownloadMetadata!.Hash;
        }
        else
        {
            _logger.LogInformation("Downloading {hash}", modList.Links.MachineURL);
            await _discord.SendAsync(Channel.Ham,
                $"Downloading and ingesting {modList.Title} ({modList.Links.MachineURL}) v{modList.Version}", token);
            return await DownloadWabbajackFile(modList, archiveManager, token);
        }
    }

    private async Task<Hash> DownloadWabbajackFile(ModlistMetadata modList, ArchiveManager archiveManager,
        CancellationToken token)
    {
        var state = _dispatcher.Parse(new Uri(modList.Links.Download));
        if (state == null)
            _logger.LogCritical("Can't download {url}", modList.Links.Download);

        var archive = new Archive
        {
            State = state!,
            Size = modList.DownloadMetadata!.Size,
            Hash = modList.DownloadMetadata.Hash
        };

        await using var tempFile = _temporaryFileManager.CreateFile(Ext.Wabbajack);
        _logger.LogInformation("Downloading {primaryKeyString}", state.PrimaryKeyString);
        var hash = await _dispatcher.Download(archive, tempFile.Path, token);

        if (hash != modList.DownloadMetadata.Hash)
        {
            _logger.LogCritical("Downloaded modlist was {actual} expected {expected}", hash,
                modList.DownloadMetadata.Hash);
            throw new Exception();
        }

        _logger.LogInformation("Archiving {hash}", hash);
        await archiveManager.Ingest(tempFile.Path, token);
        return hash;
    }

    public async ValueTask<HashSet<Hash>> AllMirroredFiles(CancellationToken token)
    {
        using var client = await GetMirrorFtpClient(token);
        using var job = await _ftpRateLimiter.Begin("Getting mirror list", 0, token);
        var files = await client.GetListingAsync(token);
        var parsed = files.TryKeep(f => (Hash.TryGetFromHex(f.Name, out var hash), hash)).ToHashSet();
        return parsed;
    }

    public async ValueTask<HashSet<(Hash, Hash)>> AllPatchFiles(CancellationToken token)
    {
        using var client = await GetPatchesFtpClient(token);
        using var job = await _ftpRateLimiter.Begin("Getting patches list", 0, token);
        var files = await client.GetListingAsync(token);
        var parsed = files.TryKeep(f =>
            {
                var parts = f.Name.Split("_");
                return (parts.Length == 2, parts);
            })
            .TryKeep(p => (Hash.TryGetFromHex(p[0], out var fromHash) &
                           Hash.TryGetFromHex(p[1], out var toHash),
                (fromHash, toHash)))
            .ToHashSet();
        return parsed;
    }

    private async Task<FtpClient> GetMirrorFtpClient(CancellationToken token)
    {
        var client = await (await _ftpSiteCredentials.Get())![StorageSpace.Mirrors].GetClient(_logger);
        await client.ConnectAsync(token);
        return client;
    }

    private async Task<FtpClient> GetPatchesFtpClient(CancellationToken token)
    {
        var client = await (await _ftpSiteCredentials.Get())![StorageSpace.Patches].GetClient(_logger);
        await client.ConnectAsync(token);
        return client;
    }
}