#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Moddy.Data;
using StardewModdingAPI;

namespace Moddy.Services
{

    public static class UpdateChecker
    {
        public static readonly Dictionary<string, GitHubRelease?> LatestReleases = new();
        public static readonly Dictionary<string, string?> LatestNexusVersions = new();

        public static async Task CheckAllAsync()
        {
            var registry = new ModRegistry(ModEntry.ModDirectoryPath);
            var scanner = new InstalledModScanner();
            var installedMods = scanner.Scan();

            foreach (var kvp in registry.Mods)
            {
                var catalogKey = kvp.Key;
                var info = kvp.Value;

                if (!info.AutoUpdate || info.LockedVersion != null)
                    continue;

                try
                {
                    if (catalogKey.StartsWith("nexus:", StringComparison.Ordinal))
                    {
                        // Nexus mod
                        if (!NexusApiClient.IsValidated)
                            continue;

                        var modIdStr = catalogKey["nexus:".Length..];
                        if (!int.TryParse(modIdStr, out var modId))
                            continue;

                        var files = await NexusApiClient.GetModFilesAsync(modId);
                        var mainFile = files?.FirstOrDefault(f =>
                            "MAIN".Equals(f.CategoryName, StringComparison.OrdinalIgnoreCase));

                        if (mainFile == null)
                            continue;

                        LatestNexusVersions[catalogKey] = mainFile.Version;

                        var latestVersion = NormalizeVersion(mainFile.Version);
                        var installedVersion = NormalizeVersion(info.InstalledVersion);

                        if (latestVersion != null && installedVersion != null && latestVersion > installedVersion)
                        {
                            ModEntry.Logger.Log(
                                $"Update available for {catalogKey}: {info.InstalledVersion} -> {mainFile.Version}",
                                LogLevel.Info);
                        }
                    }
                    else
                    {
                        // GitHub mod
                        var parts = catalogKey.Split('/');
                        if (parts.Length != 2) continue;

                        var releases = await GitHubApiClient.GetReleasesAsync(parts[0], parts[1]);
                        var latest = releases?.FirstOrDefault(r => !r.Draft && !r.Prerelease);

                        if (latest == null) continue;

                        LatestReleases[catalogKey] = latest;

                        var latestVersion = NormalizeVersion(latest.TagName);
                        var installedVersion = NormalizeVersion(info.InstalledVersion);

                        if (latestVersion != null && installedVersion != null && latestVersion > installedVersion)
                        {
                            ModEntry.Logger.Log(
                                $"Update available for {catalogKey}: {info.InstalledVersion} -> {latest.TagName}",
                                LogLevel.Info);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ModEntry.Logger.Log($"Update check failed for {catalogKey}: {ex.Message}", LogLevel.Warn);
                }
            }
        }

        public static Version? NormalizeVersion(string? tag)
        {
            if (string.IsNullOrEmpty(tag))
                return null;

            // Strip 'v' prefix
            var cleaned = tag.TrimStart('v', 'V');

            // Try parsing
            return Version.TryParse(cleaned, out var version) ? version : null;
        }

        public static bool IsUpdateAvailable(string catalogKey, string installedVersion)
        {
            if (catalogKey.StartsWith("nexus:", StringComparison.Ordinal))
            {
                if (!LatestNexusVersions.TryGetValue(catalogKey, out var latestStr) || latestStr == null)
                    return false;

                var latestVer = NormalizeVersion(latestStr);
                var installedVer = NormalizeVersion(installedVersion);
                return latestVer != null && installedVer != null && latestVer > installedVer;
            }

            if (!LatestReleases.TryGetValue(catalogKey, out var latest) || latest == null)
                return false;

            var ghLatestVer = NormalizeVersion(latest.TagName);
            var ghInstalledVer = NormalizeVersion(installedVersion);

            return ghLatestVer != null && ghInstalledVer != null && ghLatestVer > ghInstalledVer;
        }
    }

}
