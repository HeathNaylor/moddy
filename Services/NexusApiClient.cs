#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Moddy.Data;
using StardewModdingAPI;

namespace Moddy.Services
{

    public static class NexusApiClient
    {
        private static readonly HttpClient Http = new();
        private static readonly ConcurrentDictionary<string, (object? Data, DateTime CachedAt)> MemoryCache = new();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private static int _dailyRemaining = 2500;
        private static int _hourlyRemaining = 100;
        private static string? _cacheDir;

        public static bool IsPremium { get; private set; }
        public static bool IsValidated { get; private set; }

        static NexusApiClient()
        {
            Http.BaseAddress = new Uri("https://api.nexusmods.com/v1/");
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("Moddy/1.0");
            Http.Timeout = TimeSpan.FromSeconds(15);
        }

        private static string CacheDir
        {
            get
            {
                if (_cacheDir == null)
                {
                    _cacheDir = Path.Combine(ModEntry.ModDirectoryPath, "cache", "nexus");
                    Directory.CreateDirectory(_cacheDir);
                }
                return _cacheDir;
            }
        }

        public static void SetApiKey(string? key)
        {
            Http.DefaultRequestHeaders.Remove("apikey");
            if (!string.IsNullOrWhiteSpace(key))
                Http.DefaultRequestHeaders.Add("apikey", key);
            IsValidated = false;
            IsPremium = false;
        }

        public static async Task<bool> ValidateAsync()
        {
            try
            {
                var response = await Http.GetAsync("users/validate.json");
                UpdateRateLimits(response);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<NexusValidateResponse>(json, JsonOptions);
                if (result != null)
                {
                    IsPremium = result.IsPremium;
                    IsValidated = true;
                    ModEntry.Logger.Log($"Nexus API key validated (premium: {IsPremium})", LogLevel.Info);
                    return true;
                }
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log($"Nexus API key validation failed: {ex.Message}", LogLevel.Warn);
            }

            IsValidated = false;
            return false;
        }

        public static async Task<NexusModInfo?> GetModInfoAsync(int modId)
        {
            var cacheKey = $"mod_{modId}";
            var cacheTtl = TimeSpan.FromMinutes(ModEntry.Config.CacheMinutes);

            if (TryGetMemoryCache<NexusModInfo>(cacheKey, cacheTtl, out var cached))
                return cached;

            if (!CheckRateLimit())
                return GetDiskCache<NexusModInfo>(cacheKey) ?? cached;

            try
            {
                var response = await Http.GetAsync($"games/stardewvalley/mods/{modId}.json");
                UpdateRateLimits(response);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<NexusModInfo>(json, JsonOptions);

                if (result != null)
                {
                    MemoryCache[cacheKey] = (result, DateTime.UtcNow);
                    SaveDiskCache(cacheKey, json);
                }

                return result;
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log($"Nexus API error for mod {modId}: {ex.Message}", LogLevel.Warn);
                return GetDiskCache<NexusModInfo>(cacheKey) ?? cached;
            }
        }

        public static async Task<NexusFileInfo[]?> GetModFilesAsync(int modId)
        {
            var cacheKey = $"files_{modId}";
            var cacheTtl = TimeSpan.FromMinutes(ModEntry.Config.CacheMinutes);

            if (TryGetMemoryCache<NexusFileInfo[]>(cacheKey, cacheTtl, out var cached))
                return cached;

            if (!CheckRateLimit())
                return GetDiskCache<NexusFileInfo[]>(cacheKey) ?? cached;

            try
            {
                var response = await Http.GetAsync($"games/stardewvalley/mods/{modId}/files.json");
                UpdateRateLimits(response);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var filesResponse = JsonSerializer.Deserialize<NexusFilesResponse>(json, JsonOptions);
                var files = filesResponse?.Files;

                if (files != null)
                {
                    MemoryCache[cacheKey] = (files, DateTime.UtcNow);
                    SaveDiskCache(cacheKey, json);
                }

                return files;
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log($"Nexus API error for mod {modId} files: {ex.Message}", LogLevel.Warn);
                return GetDiskCache<NexusFileInfo[]>(cacheKey) ?? cached;
            }
        }

        public static async Task<NexusDownloadLink[]?> GetDownloadLinksAsync(int modId, int fileId, string? key = null, long? expires = null)
        {
            try
            {
                var url = $"games/stardewvalley/mods/{modId}/files/{fileId}/download_link.json";
                if (!string.IsNullOrEmpty(key) && expires.HasValue)
                    url += $"?key={key}&expires={expires.Value}";

                var response = await Http.GetAsync(url);
                UpdateRateLimits(response);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<NexusDownloadLink[]>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log($"Nexus download link error for mod {modId} file {fileId}: {ex.Message}", LogLevel.Warn);
                return null;
            }
        }

        public static async Task<byte[]?> DownloadFileAsync(string cdnUrl)
        {
            try
            {
                // CDN URLs are absolute, use a separate HttpClient call
                using var request = new HttpRequestMessage(HttpMethod.Get, cdnUrl);
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                return await client.GetByteArrayAsync(cdnUrl);
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log($"Nexus download failed: {ex.Message}", LogLevel.Error);
                return null;
            }
        }

        private static void UpdateRateLimits(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("x-rl-daily-remaining", out var daily))
            {
                if (int.TryParse(string.Join("", daily), out var d))
                    _dailyRemaining = d;
            }
            if (response.Headers.TryGetValues("x-rl-hourly-remaining", out var hourly))
            {
                if (int.TryParse(string.Join("", hourly), out var h))
                    _hourlyRemaining = h;
            }
        }

        private static bool CheckRateLimit()
        {
            if (_dailyRemaining <= 1 || _hourlyRemaining <= 1)
            {
                ModEntry.Logger.Log("Nexus API rate limit exhausted, using cached data", LogLevel.Warn);
                return false;
            }
            return true;
        }

        private static bool TryGetMemoryCache<T>(string key, TimeSpan ttl, out T? result)
        {
            if (MemoryCache.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.CachedAt < ttl)
            {
                result = entry.Data is T typed ? typed : default;
                return result != null;
            }
            result = default;
            return false;
        }

        private static void SaveDiskCache(string cacheKey, string json)
        {
            try
            {
                var path = Path.Combine(CacheDir, cacheKey + ".json");
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log($"Failed to write Nexus disk cache: {ex.Message}", LogLevel.Trace);
            }
        }

        private static T? GetDiskCache<T>(string cacheKey)
        {
            try
            {
                var path = Path.Combine(CacheDir, cacheKey + ".json");
                if (!File.Exists(path))
                    return default;

                var json = File.ReadAllText(path);

                // For NexusFileInfo[], the disk cache stores a NexusFilesResponse wrapper
                if (typeof(T) == typeof(NexusFileInfo[]))
                {
                    var filesResponse = JsonSerializer.Deserialize<NexusFilesResponse>(json, JsonOptions);
                    if (filesResponse?.Files is T typed)
                        return typed;
                    // Fallback: try direct array deserialization
                }

                return JsonSerializer.Deserialize<T>(json, JsonOptions);
            }
            catch
            {
                return default;
            }
        }
    }

}
