#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using HarmonyLib;
using Moddy.Data;
using Moddy.Patches;
using Moddy.Services;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace Moddy
{

    public class ModEntry : Mod
    {
        internal static ModConfig Config = null!;
        internal static IMonitor Logger = null!;
        internal static IModHelper ModHelper = null!;
        internal static string ModDirectoryPath = null!;
        internal static List<string> PendingInstalls { get; } = new();

        private int _nxmPollTicks;
        private ModRegistry? _registry;

        public override void Entry(IModHelper helper)
        {
            Config = helper.ReadConfig<ModConfig>();
            Logger = Monitor;
            ModHelper = helper;
            ModDirectoryPath = helper.DirectoryPath;

            // Clean up .old files from a previous self-update
            CleanupOldFiles();

            var harmony = new Harmony(ModManifest.UniqueID);
            TitleMenuPatch.Apply(harmony);

            // Initialize Nexus API client with saved key
            if (!string.IsNullOrWhiteSpace(Config.NexusApiKey))
            {
                NexusApiClient.SetApiKey(Config.NexusApiKey);
            }

            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;

            Logger.Log("Moddy loaded", LogLevel.Info);
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            var scanner = new InstalledModScanner();
            var installed = scanner.Scan();
            Logger.Log($"Found {installed.Count} installed mods", LogLevel.Debug);

            if (Config.CheckForUpdatesOnLaunch)
            {
                _ = UpdateChecker.CheckAllAsync();
            }

            // Validate Nexus API key
            if (!string.IsNullOrWhiteSpace(Config.NexusApiKey))
            {
                _ = NexusApiClient.ValidateAsync();
            }

            // Initialize nxm queue directory
            NxmQueueWatcher.EnsureQueueDir();

            // Register nxm handler if needed
            RegisterNxmHandlerIfNeeded();

            // Initialize registry for nxm installs
            _registry = new ModRegistry(ModDirectoryPath);
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            // Poll nxm queue every 60 ticks (~1 second)
            _nxmPollTicks++;
            if (_nxmPollTicks < 60)
                return;
            _nxmPollTicks = 0;

            if (!NexusApiClient.IsValidated)
                return;

            var requests = NxmQueueWatcher.DrainQueue();
            foreach (var req in requests)
            {
                Logger.Log($"Processing nxm request: mod {req.ModId}, file {req.FileId}", LogLevel.Info);
                _ = ProcessNxmRequest(req);
            }
        }

        private async Task ProcessNxmRequest(NxmRequest request)
        {
            try
            {
                // Get download links using the nxm key+expires
                var links = await NexusApiClient.GetDownloadLinksAsync(
                    request.ModId, request.FileId, request.Key, request.Expires);

                if (links == null || links.Length == 0)
                {
                    Logger.Log($"No download links for nxm mod {request.ModId} file {request.FileId}", LogLevel.Warn);
                    return;
                }

                // Download the file
                Logger.Log($"Downloading from Nexus CDN...", LogLevel.Info);
                var data = await NexusApiClient.DownloadFileAsync(links[0].URI);
                if (data == null)
                {
                    Logger.Log("Download failed", LogLevel.Error);
                    return;
                }

                // Find or create a CatalogEntry for this mod
                var entry = FindOrCreateCatalogEntry(request.ModId);

                // Get file info for version
                var files = await NexusApiClient.GetModFilesAsync(request.ModId);
                var fileInfo = files?.FirstOrDefault(f => f.FileId == request.FileId);
                var version = fileInfo?.Version ?? "unknown";

                // Install
                var registry = _registry ?? new ModRegistry(ModDirectoryPath);
                var success = await ModInstaller.InstallFromNexusAsync(entry, data, version, registry);

                if (success)
                {
                    Logger.Log($"Installed {entry.DisplayName} v{version} from nxm:// link", LogLevel.Info);
                    PendingInstalls.Add(entry.DisplayName);
                }
                else
                    Logger.Log($"Failed to install {entry.DisplayName} from nxm:// link", LogLevel.Error);
            }
            catch (Exception ex)
            {
                Logger.Log($"nxm install error for mod {request.ModId}: {ex.Message}", LogLevel.Error);
            }
        }

        private CatalogEntry FindOrCreateCatalogEntry(int nexusModId)
        {
            // Try to load from nexus catalog
            try
            {
                var nexusPath = Path.Combine(ModDirectoryPath, "Data", "nexus_catalog.json");
                if (!File.Exists(nexusPath))
                    nexusPath = Path.Combine(ModDirectoryPath, "nexus_catalog.json");

                if (File.Exists(nexusPath))
                {
                    var json = File.ReadAllText(nexusPath);
                    var catalog = System.Text.Json.JsonSerializer.Deserialize<CatalogEntry[]>(json);
                    var match = catalog?.FirstOrDefault(e => e.NexusModId == nexusModId);
                    if (match != null)
                        return match;
                }
            }
            catch { }

            // Create a minimal entry — will be populated after install
            return new CatalogEntry
            {
                Source = ModSource.Nexus,
                NexusModId = nexusModId,
                DisplayName = $"Nexus Mod {nexusModId}",
                Description = "Installed via nxm:// link",
                UniqueID = ""
            };
        }

        private void RegisterNxmHandlerIfNeeded()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    RegisterNxmHandlerMacOS();
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    RegisterNxmHandlerWindows();
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    RegisterNxmHandlerLinux();
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to register nxm handler: {ex.Message}", LogLevel.Warn);
            }
        }

        private void RegisterNxmHandlerMacOS()
        {
            var handlerSource = Path.Combine(ModDirectoryPath, "NxmHandler", "ModdyNxmHandler.app");
            var moddyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Moddy");
            var handlerDest = Path.Combine(moddyDir, "ModdyNxmHandler.app");

            if (!Directory.Exists(handlerSource))
            {
                Logger.Log("NxmHandler .app not found in mod directory, skipping registration", LogLevel.Trace);
                return;
            }

            if (Directory.Exists(handlerDest))
                return;

            Directory.CreateDirectory(moddyDir);
            CopyDirectory(handlerSource, handlerDest);

            var binary = Path.Combine(handlerDest, "Contents", "MacOS", "nxm-handler");
            if (File.Exists(binary))
                RunProcess("chmod", $"+x \"{binary}\"");

            RunProcess("open", $"\"{handlerDest}\"");

            Logger.Log("Registered nxm:// URL handler (macOS).", LogLevel.Info);
        }

        private void RegisterNxmHandlerWindows()
        {
            var handlerSource = Path.Combine(ModDirectoryPath, "NxmHandler", "nxm-handler.bat");
            var moddyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Moddy");
            var handlerDest = Path.Combine(moddyDir, "nxm-handler.bat");

            if (!File.Exists(handlerSource))
            {
                Logger.Log("nxm-handler.bat not found in mod directory, skipping registration", LogLevel.Trace);
                return;
            }

            // Always update the handler script
            Directory.CreateDirectory(moddyDir);
            File.Copy(handlerSource, handlerDest, overwrite: true);

            // Register nxm:// protocol in HKCU (no admin required)
            var escapedPath = handlerDest.Replace("\\", "\\\\");
            var commandValue = $"\\\"{handlerDest}\\\" \\\"%1\\\"";
            RunProcess("reg", "add \"HKCU\\Software\\Classes\\nxm\" /ve /d \"URL:NXM Protocol\" /f");
            RunProcess("reg", "add \"HKCU\\Software\\Classes\\nxm\" /v \"URL Protocol\" /d \"\" /f");
            RunProcess("reg", $"add \"HKCU\\Software\\Classes\\nxm\\shell\\open\\command\" /ve /d \"{commandValue}\" /f");

            Logger.Log("Registered nxm:// URL handler (Windows).", LogLevel.Info);
        }

        private void RegisterNxmHandlerLinux()
        {
            var handlerSource = Path.Combine(ModDirectoryPath, "NxmHandler", "nxm-handler.sh");
            var moddyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Moddy");
            var handlerDest = Path.Combine(moddyDir, "nxm-handler.sh");

            if (!File.Exists(handlerSource))
            {
                Logger.Log("nxm-handler.sh not found in mod directory, skipping registration", LogLevel.Trace);
                return;
            }

            // Always update the handler script
            Directory.CreateDirectory(moddyDir);
            File.Copy(handlerSource, handlerDest, overwrite: true);
            RunProcess("chmod", $"+x \"{handlerDest}\"");

            // Create .desktop file for the handler
            var appsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "applications");
            Directory.CreateDirectory(appsDir);

            var desktopFile = Path.Combine(appsDir, "moddy-nxm-handler.desktop");
            File.WriteAllText(desktopFile,
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                "Name=Moddy NXM Handler\n" +
                $"Exec=\"{handlerDest}\" %u\n" +
                "MimeType=x-scheme-handler/nxm;\n" +
                "NoDisplay=true\n");

            // Register as default handler for nxm:// URLs
            RunProcess("xdg-mime", "default moddy-nxm-handler.desktop x-scheme-handler/nxm");

            Logger.Log("Registered nxm:// URL handler (Linux).", LogLevel.Info);
        }

        private void CleanupOldFiles()
        {
            try
            {
                var oldFiles = Directory.GetFiles(ModDirectoryPath, "*.old", SearchOption.AllDirectories);
                foreach (var file in oldFiles)
                {
                    try
                    {
                        File.Delete(file);
                        Logger.Log($"Cleaned up old file: {Path.GetFileName(file)}", LogLevel.Trace);
                    }
                    catch { }
                }

                if (oldFiles.Length > 0)
                    Logger.Log($"Self-update cleanup: removed {oldFiles.Length} old file(s)", LogLevel.Info);
            }
            catch { }
        }

        private static void RunProcess(string fileName, string arguments, int timeoutMs = 5000)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            })?.WaitForExit(timeoutMs);
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (var file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            }

            foreach (var dir in Directory.GetDirectories(source))
            {
                CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
            }
        }
    }

}
