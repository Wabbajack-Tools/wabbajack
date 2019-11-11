﻿using ReactiveUI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Security.Authentication;
using System.Text;
using System.Threading.Tasks;
using Wabbajack.Common;
using Wabbajack.Lib.Downloaders;
using WebSocketSharp;
using static Wabbajack.Lib.NexusApi.NexusApiUtils;

namespace Wabbajack.Lib.NexusApi
{
    public class NexusApiClient : ViewModel
    {
        private static readonly string API_KEY_CACHE_FILE = "nexus.key_cache";

        private readonly HttpClient _httpClient;


        #region Authentication

        private readonly string _apiKey;

        public bool IsAuthenticated => _apiKey != null;

        private UserStatus _userStatus;

        public UserStatus UserStatus
        {
            get
            {
                if (_userStatus == null)
                    _userStatus = GetUserStatus();
                return _userStatus;
            }
        }

        public bool IsPremium => IsAuthenticated && UserStatus.is_premium;

        public string Username => UserStatus?.name;


        private static object _getAPIKeyLock = new object();
        private static string GetApiKey()
        {
            lock (_getAPIKeyLock)
            {
                // check if there exists a cached api key
                var fi = new FileInfo(API_KEY_CACHE_FILE);
                if (fi.Exists)
                {
                    return File.ReadAllText(API_KEY_CACHE_FILE);
                }

                var env_key = Environment.GetEnvironmentVariable("NEXUSAPIKEY");
                if (env_key != null)
                {
                    return env_key;
                }

                // open a web socket to receive the api key
                var guid = Guid.NewGuid();
                var _websocket = new WebSocket("wss://sso.nexusmods.com")
                {
                    SslConfiguration =
                    {
                        EnabledSslProtocols = SslProtocols.Tls12
                    }
                };

                var api_key = new TaskCompletionSource<string>();
                _websocket.OnMessage += (sender, msg) => { api_key.SetResult(msg.Data); };

                _websocket.Connect();
                _websocket.Send("{\"id\": \"" + guid + "\", \"appid\": \"" + Consts.AppName + "\"}");

                // open a web browser to get user permission
                Process.Start($"https://www.nexusmods.com/sso?id={guid}&application=" + Consts.AppName);

                // get the api key from the socket and cache it
                api_key.Task.Wait();
                var result = api_key.Task.Result;
                File.WriteAllText(API_KEY_CACHE_FILE, result);

                return result;
            }
        }

        public UserStatus GetUserStatus()
        {
            var url = "https://api.nexusmods.com/v1/users/validate.json";
            return Get<UserStatus>(url);
        }

        #endregion

        #region Rate Tracking

        private readonly object RemainingLock = new object();

        private int _dailyRemaining;
        public int DailyRemaining
        {
            get
            {
                lock (RemainingLock)
                {
                    return _dailyRemaining;
                }
            }
        }

        private int _hourlyRemaining;
        public int HourlyRemaining
        {
            get
            {
                lock (RemainingLock)
                {
                    return _hourlyRemaining;
                }
            }
        }


        private void UpdateRemaining(HttpResponseMessage response)
        {
            try
            {
                var dailyRemaining = int.Parse(response.Headers.GetValues("x-rl-daily-remaining").First());
                var hourlyRemaining = int.Parse(response.Headers.GetValues("x-rl-hourly-remaining").First());

                lock (RemainingLock)
                {
                    _dailyRemaining = Math.Min(dailyRemaining, hourlyRemaining);
                    _hourlyRemaining = Math.Min(dailyRemaining, hourlyRemaining);
                }
                this.RaisePropertyChanged(nameof(DailyRemaining));
                this.RaisePropertyChanged(nameof(HourlyRemaining));
            }
            catch (Exception)
            {
            }

        }

        #endregion


        public NexusApiClient()
        {
            _apiKey = GetApiKey();
            _httpClient = new HttpClient();

            // set default headers for all requests to the Nexus API
            var headers = _httpClient.DefaultRequestHeaders;
            headers.Add("User-Agent", Consts.UserAgent);
            headers.Add("apikey", _apiKey);
            headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            headers.Add("Application-Name", Consts.AppName);
            headers.Add("Application-Version", $"{Assembly.GetEntryAssembly()?.GetName()?.Version ?? new Version(0, 1)}");

            if (!Directory.Exists(Consts.NexusCacheDirectory))
                Directory.CreateDirectory(Consts.NexusCacheDirectory);
        }

        private T Get<T>(string url)
        {
            Task<HttpResponseMessage> responseTask = _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            responseTask.Wait();

            var response = responseTask.Result;
            UpdateRemaining(response);

            var contentTask = response.Content.ReadAsStreamAsync();
            contentTask.Wait();

            using (var stream = contentTask.Result)
            {
                return stream.FromJSON<T>();
            }
        }

        private T GetCached<T>(string url)
        {
            var code = Encoding.UTF8.GetBytes(url).ToHex() + ".json";

            if (UseLocalCache)
            {
                if (!Directory.Exists(LocalCacheDir))
                    Directory.CreateDirectory(LocalCacheDir);

                var cache_file = Path.Combine(LocalCacheDir, code);
                if (File.Exists(cache_file))
                {
                    return cache_file.FromJSON<T>();
                }

                var result = Get<T>(url);
                result.ToJSON(cache_file);
                return result;
            }

            try
            {
                return Get<T>(Consts.WabbajackCacheLocation + code);
            }
            catch (Exception)
            {
                return Get<T>(url);
            }

        }


        public string GetNexusDownloadLink(NexusDownloader.State archive, bool cache = false)
        {
            if (cache && TryGetCachedLink(archive, out var result))
                return result;

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            var url = $"https://api.nexusmods.com/v1/games/{ConvertGameName(archive.GameName)}/mods/{archive.ModID}/files/{archive.FileID}/download_link.json";
            return Get<List<DownloadLink>>(url).First().URI;
        }

        private bool TryGetCachedLink(NexusDownloader.State archive, out string result)
        {
            if (!Directory.Exists(Consts.NexusCacheDirectory))
                Directory.CreateDirectory(Consts.NexusCacheDirectory);

            var path = Path.Combine(Consts.NexusCacheDirectory, $"link-{archive.GameName}-{archive.ModID}-{archive.FileID}.txt");
            if (!File.Exists(path) || (DateTime.Now - new FileInfo(path).LastWriteTime).TotalHours > 24)
            {
                File.Delete(path);
                result = GetNexusDownloadLink(archive);
                File.WriteAllText(path, result);
                return true;
            }

            result = File.ReadAllText(path);
            return true;
        }

        public NexusFileInfo GetFileInfo(NexusDownloader.State mod)
        {
            var url = $"https://api.nexusmods.com/v1/games/{ConvertGameName(mod.GameName)}/mods/{mod.ModID}/files/{mod.FileID}.json";
            return GetCached<NexusFileInfo>(url);
        }

        public class GetModFilesResponse
        {
            public List<NexusFileInfo> files;
        }

        public IList<NexusFileInfo> GetModFiles(Game game, int modid)
        {
            var url = $"https://api.nexusmods.com/v1/games/{GameRegistry.Games[game].NexusName}/mods/{modid}/files.json";
            return GetCached<GetModFilesResponse>(url).files;
        }

        public ModInfo GetModInfo(Game game, string modId)
        {
            var url = $"https://api.nexusmods.com/v1/games/{GameRegistry.Games[game].NexusName}/mods/{modId}.json";
            return GetCached<ModInfo>(url);
        }

        public EndorsementResponse EndorseMod(NexusDownloader.State mod)
        {
            Utils.Status($"Endorsing ${mod.GameName} - ${mod.ModID}");
            var url = $"https://api.nexusmods.com/v1/games/{ConvertGameName(mod.GameName)}/mods/{mod.ModID}/endorse.json";

            var content = new FormUrlEncodedContent(new Dictionary<string, string> { { "version", mod.Version } });

            using (var stream = _httpClient.PostStreamSync(url, content))
            {
                return stream.FromJSON<EndorsementResponse>();
            }
        }

        private class DownloadLink
        {
            public string URI { get; set; }
        }

        private class UpdatedMod
        {
            public long mod_id;
            public long latest_file_update;
            public long latest_mod_activity;
        }

        private static bool? _useLocalCache;
        public static bool UseLocalCache
        {
            get
            {
                if (_useLocalCache == null) return LocalCacheDir != null;
                return _useLocalCache ?? false;
            }
            set => _useLocalCache = value;
        }

        private static string _localCacheDir;
        public static string LocalCacheDir
        {
            get
            {
                if (_localCacheDir == null)
                    _localCacheDir = Environment.GetEnvironmentVariable("NEXUSCACHEDIR");
                return _localCacheDir;
            }
            set => _localCacheDir = value;
        }

        public void ClearUpdatedModsInCache()
        {
            if (!UseLocalCache) return;

            var purge = GameRegistry.Games.Values
                .Where(game => game.NexusName != null)
                .Select(game => new
                {
                    game = game,
                    mods = Get<List<UpdatedMod>>(
                        $"https://api.nexusmods.com/v1/games/{game.NexusName}/mods/updated.json?period=1m")
                })
                .SelectMany(r => r.mods.Select(mod => new {game = r.game, 
                                                           mod = mod}))
                .ToList();

            Utils.Log($"Found {purge.Count} updated mods in the last month");
            
            var to_purge = Directory.EnumerateFiles(LocalCacheDir, "*.json")
                .Select(f =>
                {
                    Utils.Status("Cleaning Nexus cache for");
                    var uri = new Uri(Encoding.UTF8.GetString(Path.GetFileNameWithoutExtension(f).FromHex()));
                    var parts = uri.PathAndQuery.Split('/', '.').ToHashSet();
                    var found = purge.FirstOrDefault(p => parts.Contains(p.game.NexusName) && parts.Contains(p.mod.mod_id.ToString()));
                    if (found != null)
                    {
                        var should_remove = File.GetLastWriteTimeUtc(f) <= found.mod.latest_file_update.AsUnixTime();
                        return (should_remove, f);
                    }

                    return (false, f);
                })
                .Where(p => p.Item1)
                .ToList();

            Utils.Log($"Purging {to_purge.Count} cache entries");
            to_purge.PMap(f => File.Delete(f.f));

        }
    }

}