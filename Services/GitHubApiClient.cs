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

    public static class GitHubApiClient
    {
        private static readonly HttpClient Http = new();
        private static readonly ConcurrentDictionary<string, (GitHubRelease[]? Data, DateTime CachedAt)> MemoryCache = new();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private static int _rateLimitRemaining = 60;
        private static DateTime _rateLimitReset = DateTime.MinValue;
        private static string? _cacheDir;

        static GitHubApiClient()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("Moddy/1.0");
            Http.Timeout = TimeSpan.FromSeconds(15);
        }

        private static string CacheDir
        {
            get
            {
                if (_cacheDir == null)
                {
                    _cacheDir = Path.Combine(ModEntry.ModDirectoryPath, "cache");
                    Directory.CreateDirectory(_cacheDir);
                }
                return _cacheDir;
            }
        }

        public static async Task<GitHubRelease[]?> GetReleasesAsync(string owner, string repo, int perPage = 5)
        {
            var cacheKey = $"{owner}/{repo}";
            var cacheTtl = TimeSpan.FromMinutes(ModEntry.Config.CacheMinutes);

            // Check memory cache
            if (MemoryCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.CachedAt < cacheTtl)
                return cached.Data;

            // Check rate limit
            if (_rateLimitRemaining <= 1 && DateTime.UtcNow < _rateLimitReset)
            {
                ModEntry.Logger.Log("GitHub API rate limit exhausted, using cached data", LogLevel.Warn);
                return GetDiskCache(cacheKey) ?? cached.Data;
            }

            try
            {
                var url = $"https://api.github.com/repos/{owner}/{repo}/releases?per_page={perPage}";
                var response = await Http.GetAsync(url);

                // Update rate limit info
                if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining))
                {
                    if (int.TryParse(string.Join("", remaining), out var rem))
                        _rateLimitRemaining = rem;
                }
                if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset))
                {
                    if (long.TryParse(string.Join("", reset), out var epoch))
                        _rateLimitReset = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
                }

                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                var releases = JsonSerializer.Deserialize<GitHubRelease[]>(json, JsonOptions);

                // Update caches
                if (releases != null)
                {
                    MemoryCache[cacheKey] = (releases, DateTime.UtcNow);
                    SaveDiskCache(cacheKey, json);
                }

                return releases;
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log($"GitHub API error for {cacheKey}: {ex.Message}", LogLevel.Warn);
                // Fall back to disk cache, then stale memory cache
                return GetDiskCache(cacheKey) ?? cached.Data;
            }
        }

        public static async Task<byte[]?> DownloadAssetAsync(string url)
        {
            try
            {
                return await Http.GetByteArrayAsync(url);
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log($"Download failed: {ex.Message}", LogLevel.Error);
                return null;
            }
        }

        private static void SaveDiskCache(string cacheKey, string json)
        {
            try
            {
                var path = GetDiskCachePath(cacheKey);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log($"Failed to write disk cache: {ex.Message}", LogLevel.Trace);
            }
        }

        private static GitHubRelease[]? GetDiskCache(string cacheKey)
        {
            try
            {
                var path = GetDiskCachePath(cacheKey);
                if (!File.Exists(path))
                    return null;

                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<GitHubRelease[]>(json, JsonOptions);
            }
            catch
            {
                return null;
            }
        }

        private static string GetDiskCachePath(string cacheKey)
        {
            // Replace / with _ for filesystem safety
            return Path.Combine(CacheDir, cacheKey.Replace('/', '_') + ".json");
        }
    }

}
