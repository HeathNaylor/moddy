#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewModdingAPI;

namespace Moddy.Services
{

    public class ScannedMod
    {
        public string UniqueID { get; set; } = "";
        public string Version { get; set; } = "";
        public string Name { get; set; } = "";
        public string FolderName { get; set; } = "";
        public string Author { get; set; } = "";
        public string Description { get; set; } = "";
    }

    internal class ModManifestData
    {
        [JsonPropertyName("UniqueID")]
        public string UniqueID { get; set; } = "";

        [JsonPropertyName("Version")]
        public string Version { get; set; } = "";

        [JsonPropertyName("Name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("Author")]
        public string Author { get; set; } = "";

        [JsonPropertyName("Description")]
        public string Description { get; set; } = "";
    }

    public class InstalledModScanner
    {
        public Dictionary<string, ScannedMod> Scan()
        {
            var result = new Dictionary<string, ScannedMod>(StringComparer.OrdinalIgnoreCase);
            var modsDir = Path.Combine(Constants.GamePath, "Mods");

            if (!Directory.Exists(modsDir))
                return result;

            foreach (var dir in Directory.GetDirectories(modsDir))
            {
                var manifestPath = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifestPath))
                    continue;

                try
                {
                    var json = File.ReadAllText(manifestPath);
                    var manifest = JsonSerializer.Deserialize<ModManifestData>(json);
                    if (manifest == null || string.IsNullOrEmpty(manifest.UniqueID))
                        continue;

                    result[manifest.UniqueID] = new ScannedMod
                    {
                        UniqueID = manifest.UniqueID,
                        Version = manifest.Version,
                        Name = manifest.Name,
                        FolderName = Path.GetFileName(dir),
                        Author = manifest.Author,
                        Description = manifest.Description
                    };
                }
                catch (Exception ex)
                {
                    ModEntry.Logger.Log($"Failed to read manifest in {Path.GetFileName(dir)}: {ex.Message}", LogLevel.Trace);
                }
            }

            return result;
        }
    }

}
