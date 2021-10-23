﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Wabbajack.DTOs;
using Wabbajack.Hashing.xxHash64;
using Wabbajack.Server.DTOs;

namespace Wabbajack.Server.DataLayer;

public partial class SqlService
{
    public async Task<MirroredFile> GetNextMirroredFile()
    {
        await using var conn = await Open();
        var result = await conn.QueryFirstOrDefaultAsync<(Hash, DateTime, DateTime, string, string)>(
            "SELECT Hash, Created, Uploaded, Rationale, FailMessage from dbo.MirroredArchives WHERE Uploaded IS NULL");
        if (result == default) return null;
        return new MirroredFile
        {
            Hash = result.Item1, Created = result.Item2, Uploaded = result.Item3, Rationale = result.Item4,
            FailMessage = result.Item5
        };
    }

    public async Task<Dictionary<Hash, bool>> GetAllMirroredHashes()
    {
        await using var conn = await Open();
        return (await conn.QueryAsync<(Hash, DateTime?)>("SELECT Hash, Uploaded FROM dbo.MirroredArchives"))
            .GroupBy(d => d.Item1)
            .ToDictionary(d => d.Key, d => d.First().Item2.HasValue);
    }

    public async Task StartMirror((Hash Hash, string Reason) mirror)
    {
        await using var conn = await Open();
        await using var trans = await conn.BeginTransactionAsync();

        if (await conn.QueryFirstOrDefaultAsync<Hash>(@"SELECT Hash FROM dbo.MirroredArchives WHERE Hash = @Hash",
                new {mirror.Hash}, trans) != default)
            return;

        await conn.ExecuteAsync(
            @"INSERT INTO dbo.MirroredArchives (Hash, Created, Rationale) VALUES (@Hash, GETUTCDATE(), @Reason)",
            new {mirror.Hash, mirror.Reason}, trans);
        await trans.CommitAsync();
    }

    public async Task<Dictionary<Hash, string>> GetAllowedMirrors()
    {
        await using var conn = await Open();
        return (await conn.QueryAsync<(Hash, string)>("SELECT Hash, Reason FROM dbo.AllowedMirrorsCache"))
            .GroupBy(d => d.Item1)
            .ToDictionary(d => d.Key, d => d.First().Item2);
    }

    public async Task UpsertMirroredFile(MirroredFile file)
    {
        await using var conn = await Open();
        await using var trans = await conn.BeginTransactionAsync();

        await conn.ExecuteAsync("DELETE FROM dbo.MirroredArchives WHERE Hash = @Hash", new {file.Hash}, trans);
        await conn.ExecuteAsync(
            "INSERT INTO dbo.MirroredArchives (Hash, Created, Uploaded, Rationale, FailMessage) VALUES (@Hash, @Created, @Uploaded, @Rationale, @FailMessage)",
            new
            {
                file.Hash,
                file.Created,
                file.Uploaded,
                file.Rationale,
                file.FailMessage
            }, trans);
        await trans.CommitAsync();
    }

    public async Task DeleteMirroredFile(Hash hash)
    {
        await using var conn = await Open();
        await conn.ExecuteAsync("DELETE FROM dbo.MirroredArchives WHERE Hash = @Hash",
            new {Hash = hash});
    }

    public async Task<bool> HaveMirror(Hash hash)
    {
        await using var conn = await Open();

        return await conn.QueryFirstOrDefaultAsync<Hash>("SELECT Hash FROM dbo.MirroredArchives WHERE Hash = @Hash",
            new {Hash = hash}) != default;
    }

    public async Task QueueMirroredFiles()
    {
        await using var conn = await Open();

        await conn.ExecuteAsync(@"

                INSERT INTO dbo.MirroredArchives (Hash, Created, Rationale)

                SELECT hs.Hash, GETUTCDATE(), 'File has re-upload permissions on the Nexus' FROM
                (SELECT DISTINCT ad.Hash FROM dbo.NexusModPermissions p
                INNER JOIN GameMetadata md on md.NexusGameId = p.NexusGameID
                INNER JOIN dbo.ArchiveDownloads ad on ad.PrimaryKeyString like 'NexusDownloader+State|'+md.WabbajackName+'|'+CAST(p.ModID as nvarchar)+'|%'
                WHERE p.Permissions = 1
                AND ad.Hash not in (SELECT Hash from dbo.MirroredArchives)
                ) hs

                INSERT INTO dbo.MirroredArchives (Hash, Created, Rationale)
                SELECT DISTINCT Hash, GETUTCDATE(), 'File is hosted on GitHub'
                FROM dbo.ArchiveDownloads ad WHERE PrimaryKeyString like '%github.com/%'
                AND ad.Hash not in (SELECT Hash from dbo.MirroredArchives)


                INSERT INTO dbo.MirroredArchives (Hash, Created, Rationale)
                SELECT DISTINCT Hash, GETUTCDATE(), 'File license allows uploading to any Non-nexus site'
                FROM dbo.ArchiveDownloads ad WHERE PrimaryKeyString like '%enbdev.com/%'
                AND ad.Hash not in (SELECT Hash from dbo.MirroredArchives)

                INSERT INTO dbo.MirroredArchives (Hash, Created, Rationale)
                SELECT DISTINCT Hash, GETUTCDATE(), 'DynDOLOD file' /*, Name*/
                from dbo.ModListArchives mla WHERE Name like '%DynDoLOD%standalone%'
                and Hash not in (select Hash from dbo.MirroredArchives)

                INSERT INTO dbo.MirroredArchives (Hash, Created, Rationale)
                SELECT DISTINCT Hash, GETUTCDATE(), 'Distribution allowed by author' /*, Name*/
                from dbo.ModListArchives mla WHERE Name like '%particle%patch%'
                and Hash not in (select Hash from dbo.MirroredArchives)


                ");
    }

    public async Task AddNexusModWithOpenPerms(Game gameGame, long modId)
    {
        await using var conn = await Open();

        await conn.ExecuteAsync(
            @"INSERT INTO dbo.NexusModsWithOpenPerms(NexusGameID, NexusModID) VALUES(@game, @mod)",
            new {game = gameGame.MetaData().NexusGameId, modId});
    }

    public async Task SyncActiveMirroredFiles()
    {
        await using var conn = await Open();
        await conn.ExecuteAsync(@"EXEC dbo.QueueMirroredFiles");
    }
}