#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moddy.Data
{

    public class InstalledModInfo
    {
        [JsonPropertyName("catalogKey")]
        public string CatalogKey { get; set; } = "";

        [JsonPropertyName("uniqueID")]
        public string UniqueID { get; set; } = "";

        [JsonPropertyName("installedVersion")]
        public string InstalledVersion { get; set; } = "";

        [JsonPropertyName("installedFolderName")]
        public string InstalledFolderName { get; set; } = "";

        [JsonPropertyName("installedAt")]
        public DateTime InstalledAt { get; set; }

        [JsonPropertyName("autoUpdate")]
        public bool AutoUpdate { get; set; } = true;

        [JsonPropertyName("lockedVersion")]
        public string? LockedVersion { get; set; }

        [JsonPropertyName("source")]
        public ModSource Source { get; set; } = ModSource.GitHub;
    }

    public class ModRegistry
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private readonly string _filePath;

        public Dictionary<string, InstalledModInfo> Mods { get; set; } = new();

        public ModRegistry(string modDirectoryPath)
        {
            _filePath = Path.Combine(modDirectoryPath, "registry.json");
            Load();
        }

        public void Load()
        {
            if (!File.Exists(_filePath))
                return;

            try
            {
                var json = File.ReadAllText(_filePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, InstalledModInfo>>(json, JsonOptions);
                if (data != null)
                    Mods = data;
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log($"Failed to load registry: {ex.Message}", StardewModdingAPI.LogLevel.Warn);
            }
        }

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(Mods, JsonOptions);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log($"Failed to save registry: {ex.Message}", StardewModdingAPI.LogLevel.Error);
            }
        }

        public void Register(string catalogKey, InstalledModInfo info)
        {
            Mods[catalogKey] = info;
            Save();
        }

        public void Unregister(string catalogKey)
        {
            Mods.Remove(catalogKey);
            Save();
        }

        public InstalledModInfo? Get(string catalogKey)
        {
            return Mods.TryGetValue(catalogKey, out var info) ? info : null;
        }
    }

}
