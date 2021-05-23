﻿using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wabbajack.BuildServer;
using Wabbajack.Common;
using Wabbajack.Common.Exceptions;
using Wabbajack.Lib.NexusApi;
using Wabbajack.Server.DataLayer;

namespace Wabbajack.Server.Services
{
    public class NexusKeyMaintainance : AbstractService<NexusKeyMaintainance, int>
    {
        private SqlService _sql;
        private string _selfKey;

        public NexusKeyMaintainance(ILogger<NexusKeyMaintainance> logger, AppSettings settings, SqlService sql, QuickSync quickSync) : base(logger, settings, quickSync, TimeSpan.FromHours(4))
        {
            _sql = sql;
        }

        public async Task<NexusApiClient> GetClient()
        {
            var keys = await _sql.GetNexusApiKeysWithCounts(1500);
            foreach (var key in keys.Where(k => k.Key != _selfKey))
            {
                try
                {
                    var client = new TrackingClient(_sql, key);
                    if (await client.IsPremium())
                        return client;

                    _logger.LogWarning($"Purging non premium key");
                    await _sql.DeleteNexusAPIKey(key.Key);
                    continue;
                }
                catch (Exception ex)
                {
                    Utils.Error($"Error getting tracking client: {ex}");
                }

            }

            var bclient = await NexusApiClient.Get();
            await bclient.GetUserStatus();
            return bclient;
        }
        
        public override async Task<int> Execute()
        {
            _selfKey ??= await Utils.FromEncryptedJson<string>("nexusapikey");
            var keys = await _sql.GetNexusApiKeysWithCounts(0);
            _logger.Log(LogLevel.Information, $"Verifying {keys.Count} API Keys");
            foreach (var key in keys)
            {
                try
                {
                    var client = new TrackingClient(_sql, key);

                    var status = await client.GetUserStatus();
                    if (!status.is_premium)
                    {
                        await _sql.DeleteNexusAPIKey(key.Key);
                        continue;
                    }

                    var (daily, hourly) = await client.GetRemainingApiCalls();
                    await _sql.SetNexusAPIKey(key.Key, daily, hourly);
                }
                catch (HttpException ex)
                {
                    _logger.Log(LogLevel.Warning, $"Nexus error, not purging API key : {ex.Message}");
                }
                catch (Exception ex)
                {
                    _logger.Log(LogLevel.Warning, $"Update error, purging API key : {ex.Message}");
                    await _sql.DeleteNexusAPIKey(key.Key);
                }
            }
            return keys.Count;
        }
    }

    public class TrackingClient : NexusApiClient
    {
        private SqlService _sql;
        public TrackingClient(SqlService sql, (string Key, int Daily, int Hourly) key) : base(key.Key)
        {
            _sql = sql;
            DailyRemaining = key.Daily;
            HourlyRemaining = key.Hourly;
        }

        protected override async Task UpdateRemaining(HttpResponseMessage response)
        {
            await base.UpdateRemaining(response);
            try
            {
                var dailyRemaining = int.Parse(response.Headers.GetValues("x-rl-daily-remaining").First());
                var hourlyRemaining = int.Parse(response.Headers.GetValues("x-rl-hourly-remaining").First());
                await _sql.SetNexusAPIKey(ApiKey, dailyRemaining, hourlyRemaining);
            }
            catch (Exception)
            {
            }
        }
    }
}
