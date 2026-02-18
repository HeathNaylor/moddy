#nullable enable
using System;
using System.Text.Json.Serialization;

namespace Moddy.Data
{

    public class NexusModInfo
    {
        [JsonPropertyName("mod_id")]
        public int ModId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = "";

        [JsonPropertyName("version")]
        public string Version { get; set; } = "";

        [JsonPropertyName("author")]
        public string Author { get; set; } = "";

        [JsonPropertyName("endorsement_count")]
        public int EndorsementCount { get; set; }

        [JsonPropertyName("mod_downloads")]
        public int ModDownloads { get; set; }

        [JsonPropertyName("picture_url")]
        public string? PictureUrl { get; set; }
    }

    public class NexusFilesResponse
    {
        [JsonPropertyName("files")]
        public NexusFileInfo[] Files { get; set; } = Array.Empty<NexusFileInfo>();
    }

    public class NexusFileInfo
    {
        [JsonPropertyName("file_id")]
        public int FileId { get; set; }

        [JsonPropertyName("category_name")]
        public string CategoryName { get; set; } = "";

        [JsonPropertyName("version")]
        public string Version { get; set; } = "";

        [JsonPropertyName("file_name")]
        public string FileName { get; set; } = "";

        [JsonPropertyName("size_in_bytes")]
        public long? SizeInBytes { get; set; }

        [JsonPropertyName("size_kb")]
        public long SizeKb { get; set; }
    }

    public class NexusDownloadLink
    {
        [JsonPropertyName("URI")]
        public string URI { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("short_name")]
        public string ShortName { get; set; } = "";
    }

    public class NexusValidateResponse
    {
        [JsonPropertyName("user_id")]
        public int UserId { get; set; }

        [JsonPropertyName("key")]
        public string Key { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("is_premium")]
        public bool IsPremium { get; set; }

        [JsonPropertyName("is_supporter")]
        public bool IsSupporter { get; set; }
    }

}
