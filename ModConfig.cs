#nullable enable

namespace Moddy
{

    public class ModConfig
    {
        public bool CheckForUpdatesOnLaunch { get; set; } = true;
        public int CacheMinutes { get; set; } = 15;
        public string? NexusApiKey { get; set; }
        public bool NexusBrowsingEnabled { get; set; } = true;
    }

}
