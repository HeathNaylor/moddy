#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Moddy.Data;
using StardewModdingAPI;

namespace Moddy.Services
{

    public static class ModInstaller
    {
        public static async Task<bool> InstallAsync(CatalogEntry entry, GitHubRelease release, ModRegistry registry)
        {
            var asset = PickAsset(entry, release);
            if (asset == null)
            {
                ModEntry.Logger.Log($"No matching ZIP asset found for {entry.DisplayName}", LogLevel.Error);
                return false;
            }

            ModEntry.Logger.Log($"Downloading {asset.Name} ({asset.Size / 1024}KB)...", LogLevel.Info);
            var data = await GitHubApiClient.DownloadAssetAsync(asset.BrowserDownloadUrl);
            if (data == null)
                return false;

            return ExtractAndRegister(data, entry, release.TagName, registry);
        }

        public static async Task<bool> InstallFromNexusAsync(CatalogEntry entry, byte[] zipData, string version, ModRegistry registry)
        {
            return await Task.Run(() => ExtractAndRegister(zipData, entry, version, registry));
        }

        public static async Task<bool> InstallFromNexusDirectAsync(CatalogEntry entry, NexusFileInfo file, ModRegistry registry)
        {
            try
            {
                ModEntry.Logger.Log($"Getting download links for {entry.DisplayName} file {file.FileName}...", LogLevel.Info);
                var links = await NexusApiClient.GetDownloadLinksAsync(entry.NexusModId, file.FileId);
                if (links == null || links.Length == 0)
                {
                    ModEntry.Logger.Log($"No download links available for {entry.DisplayName}", LogLevel.Error);
                    return false;
                }

                ModEntry.Logger.Log($"Downloading {file.FileName} ({file.SizeKb}KB)...", LogLevel.Info);
                var data = await NexusApiClient.DownloadFileAsync(links[0].URI);
                if (data == null)
                    return false;

                return ExtractAndRegister(data, entry, file.Version, registry);
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log($"Nexus direct install failed for {entry.DisplayName}: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        private static bool ExtractAndRegister(byte[] data, CatalogEntry entry, string fallbackVersion, ModRegistry registry)
        {
            try
            {
                var modsDir = Path.Combine(Constants.GamePath, "Mods");
                using var stream = new MemoryStream(data);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

                // Find the manifest.json to locate the mod root inside the ZIP
                var manifestEntry = zip.Entries.FirstOrDefault(e =>
                    e.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) &&
                    !e.FullName.Contains("__MACOSX"));

                if (manifestEntry == null)
                {
                    ModEntry.Logger.Log("No manifest.json found in ZIP", LogLevel.Error);
                    return false;
                }

                // Determine the mod folder name from the ZIP structure
                var manifestDir = Path.GetDirectoryName(manifestEntry.FullName)?.Replace('\\', '/').Trim('/') ?? "";
                var folderName = string.IsNullOrEmpty(manifestDir)
                    ? entry.DisplayName.Replace(" ", "")
                    : manifestDir.Split('/')[0];

                var targetDir = Path.Combine(modsDir, folderName);

                // Check if this is a self-update (updating Moddy itself)
                var isSelfUpdate = IsSelfUpdate(targetDir);

                if (isSelfUpdate)
                {
                    ModEntry.Logger.Log("Self-update detected — using rename strategy for locked files", LogLevel.Info);
                }
                else
                {
                    // Remove existing installation if present
                    if (Directory.Exists(targetDir))
                        Directory.Delete(targetDir, true);
                }

                // Extract
                foreach (var zipEntry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(zipEntry.Name))
                        continue; // skip directories
                    if (zipEntry.FullName.Contains("__MACOSX"))
                        continue;

                    string relativePath;
                    if (string.IsNullOrEmpty(manifestDir))
                    {
                        // Flat ZIP — put everything in folderName/
                        relativePath = Path.Combine(folderName, zipEntry.FullName);
                    }
                    else
                    {
                        // ZIP has a root folder already
                        relativePath = zipEntry.FullName;
                    }

                    var destPath = Path.Combine(modsDir, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

                    if (isSelfUpdate)
                        ExtractWithRename(zipEntry, destPath);
                    else
                        zipEntry.ExtractToFile(destPath, overwrite: true);
                }

                // Read the installed manifest for version info
                var installedManifestPath = Path.Combine(targetDir, "manifest.json");
                var installedVersion = fallbackVersion;
                var uniqueId = entry.UniqueID;

                if (File.Exists(installedManifestPath))
                {
                    try
                    {
                        var json = File.ReadAllText(installedManifestPath);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("Version", out var vProp))
                            installedVersion = vProp.GetString() ?? installedVersion;
                        if (doc.RootElement.TryGetProperty("UniqueID", out var idProp))
                            uniqueId = idProp.GetString() ?? uniqueId;
                    }
                    catch { }
                }

                // Update registry
                registry.Register(entry.CatalogKey, new InstalledModInfo
                {
                    CatalogKey = entry.CatalogKey,
                    UniqueID = uniqueId,
                    InstalledVersion = installedVersion,
                    InstalledFolderName = folderName,
                    InstalledAt = DateTime.UtcNow,
                    AutoUpdate = true,
                    Source = entry.Source
                });

                ModEntry.Logger.Log($"Installed {entry.DisplayName} v{installedVersion} to {folderName}", LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log($"Install failed for {entry.DisplayName}: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        public static bool Uninstall(CatalogEntry entry, ModRegistry registry)
        {
            var info = registry.Get(entry.CatalogKey);
            if (info == null)
                return false;

            var modPath = Path.Combine(Constants.GamePath, "Mods", info.InstalledFolderName);
            try
            {
                if (Directory.Exists(modPath))
                    Directory.Delete(modPath, true);

                registry.Unregister(entry.CatalogKey);
                ModEntry.Logger.Log($"Uninstalled {entry.DisplayName} from {info.InstalledFolderName}", LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log($"Uninstall failed for {entry.DisplayName}: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        private static bool IsSelfUpdate(string targetDir)
        {
            try
            {
                var target = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var self = Path.GetFullPath(ModEntry.ModDirectoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(target, self, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void ExtractWithRename(ZipArchiveEntry zipEntry, string destPath)
        {
            try
            {
                zipEntry.ExtractToFile(destPath, overwrite: true);
            }
            catch (IOException)
            {
                // File is locked (DLL loaded by SMAPI) — rename it aside
                var oldPath = destPath + ".old";
                try
                {
                    if (File.Exists(oldPath))
                        File.Delete(oldPath);
                    File.Move(destPath, oldPath);
                    ModEntry.Logger.Log($"Renamed locked file: {Path.GetFileName(destPath)} -> .old", LogLevel.Trace);
                }
                catch (Exception renameEx)
                {
                    ModEntry.Logger.Log($"Failed to rename locked file {Path.GetFileName(destPath)}: {renameEx.Message}", LogLevel.Warn);
                    return;
                }

                // Now extract to the original path
                zipEntry.ExtractToFile(destPath, overwrite: true);
            }
        }

        private static GitHubAsset? PickAsset(CatalogEntry entry, GitHubRelease release)
        {
            var zips = release.Assets.Where(a =>
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToArray();

            if (zips.Length == 0)
                return null;

            if (!string.IsNullOrEmpty(entry.AssetFilter))
            {
                var regex = new Regex(entry.AssetFilter, RegexOptions.IgnoreCase);
                var match = zips.FirstOrDefault(a => regex.IsMatch(a.Name));
                if (match != null)
                    return match;
            }

            // Default: pick the first ZIP
            return zips.First();
        }
    }

}
