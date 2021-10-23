﻿using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Wabbajack.DTOs;
using Wabbajack.DTOs.DownloadStates;

namespace Wabbajack.Server.DataLayer;

public partial class SqlService
{
    public async Task<List<Archive>> GetNonNexusModlistArchives()
    {
        await using var conn = await Open();
        var results = await conn.QueryAsync<(Hashing.xxHash64.Hash Hash, long Size, string State)>(
            @"SELECT Hash, Size, State FROM dbo.ModListArchives WHERE PrimaryKeyString NOT LIKE 'NexusDownloader+State|%'");
        return results.Select(r => new Archive
        {
            State = _dtos.Deserialize<IDownloadState>(r.State)!,
            Size = r.Size,
            Hash = r.Hash
        }).ToList();
    }

    public async Task UpdateNonNexusModlistArchivesStatus(IEnumerable<(Archive Archive, bool IsValid)> results)
    {
        await using var conn = await Open();
        var trans = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("DELETE FROM dbo.ModlistArchiveStatus;", transaction: trans);

        foreach (var itm in results.DistinctBy(itm => (itm.Archive.Hash, itm.Archive.State.PrimaryKeyString)))
            await conn.ExecuteAsync(
                @"INSERT INTO dbo.ModlistArchiveStatus (PrimaryKeyStringHash, PrimaryKeyString, Hash, IsValid) 
               VALUES (HASHBYTES('SHA2_256', @PrimaryKeyString), @PrimaryKeyString, @Hash, @IsValid)", new
                {
                    itm.Archive.State.PrimaryKeyString,
                    itm.Archive.Hash,
                    itm.IsValid
                }, trans);

        await trans.CommitAsync();
    }
}