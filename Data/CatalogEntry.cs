#nullable enable
using System.Text.Json.Serialization;

namespace Moddy.Data
{

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ModSource
    {
        GitHub,
        Nexus,
        Local
    }

    public class CatalogEntry
    {
        [JsonPropertyName("source")]
        public ModSource Source { get; set; } = ModSource.GitHub;

        [JsonPropertyName("owner")]
        public string Owner { get; set; } = "";

        [JsonPropertyName("repo")]
        public string Repo { get; set; } = "";

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("uniqueID")]
        public string UniqueID { get; set; } = "";

        [JsonPropertyName("stars")]
        public int Stars { get; set; }

        [JsonPropertyName("assetFilter")]
        public string? AssetFilter { get; set; }

        [JsonPropertyName("nexusModId")]
        public int NexusModId { get; set; }

        [JsonPropertyName("author")]
        public string Author { get; set; } = "";

        [JsonPropertyName("endorsements")]
        public int Endorsements { get; set; }

        [JsonIgnore]
        public string CatalogKey => Source switch
        {
            ModSource.Nexus => $"nexus:{NexusModId}",
            _ => $"{Owner}/{Repo}"
        };

        /// <summary>
        /// False for mods discovered by scanning the Mods folder that aren't in catalog.json.
        /// These mods have no GitHub repo info and cannot be installed/uninstalled/updated via Moddy.
        /// </summary>
        [JsonIgnore]
        public bool IsFromCatalog { get; set; } = true;

        [JsonIgnore]
        public int Popularity => Source == ModSource.Nexus ? Endorsements : Stars;
    }

}
