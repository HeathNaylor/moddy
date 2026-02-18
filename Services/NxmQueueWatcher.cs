#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Web;

namespace Moddy.Services
{

    public record NxmRequest(int ModId, int FileId, string? Key, long? Expires);

    public static class NxmQueueWatcher
    {
        private static readonly string QueueDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Moddy", "nxm_queue");

        public static void EnsureQueueDir()
        {
            Directory.CreateDirectory(QueueDir);
        }

        public static List<NxmRequest> DrainQueue()
        {
            var requests = new List<NxmRequest>();

            if (!Directory.Exists(QueueDir))
                return requests;

            foreach (var file in Directory.GetFiles(QueueDir, "*.nxmurl"))
            {
                try
                {
                    var url = File.ReadAllText(file).Trim();
                    File.Delete(file);

                    var parsed = ParseNxmUrl(url);
                    if (parsed != null)
                        requests.Add(parsed);
                }
                catch (Exception ex)
                {
                    try { Moddy.ModEntry.Logger.Log($"Failed to process nxm queue file {Path.GetFileName(file)}: {ex.Message}", StardewModdingAPI.LogLevel.Warn); }
                    catch { }
                }
            }

            return requests;
        }

        public static NxmRequest? ParseNxmUrl(string url)
        {
            // Format: nxm://stardewvalley/mods/{modId}/files/{fileId}?key=X&expires=Y&user_id=Z
            try
            {
                if (!url.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
                    return null;

                var uri = new Uri(url);

                // Host = "stardewvalley"
                if (!uri.Host.Equals("stardewvalley", StringComparison.OrdinalIgnoreCase))
                    return null;

                // Path = /mods/{modId}/files/{fileId}
                var segments = uri.AbsolutePath.Trim('/').Split('/');
                if (segments.Length < 4 || segments[0] != "mods" || segments[2] != "files")
                    return null;

                if (!int.TryParse(segments[1], out var modId) || !int.TryParse(segments[3], out var fileId))
                    return null;

                // Query parameters
                var query = HttpUtility.ParseQueryString(uri.Query);
                var key = query["key"];
                long? expires = null;
                if (long.TryParse(query["expires"], out var exp))
                    expires = exp;

                return new NxmRequest(modId, fileId, key, expires);
            }
            catch
            {
                return null;
            }
        }
    }

}
