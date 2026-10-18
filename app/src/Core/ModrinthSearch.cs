using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CardboardBox.Core
{
    public class ModrinthHit
    {
        public string Slug, Title, Description;
        public long Downloads;

        public string DownloadsText =>
            Downloads >= 1000000 ? (Downloads / 1000000.0).ToString("0.#") + "M" :
            Downloads >= 1000 ? (Downloads / 1000.0).ToString("0.#") + "K" : Downloads.ToString();
    }

    /// <summary>Searches Modrinth for plugins / mods that work with the server type and Minecraft version.</summary>
    public static class ModrinthSearch
    {
        public static async Task<List<ModrinthHit>> SearchAsync(string query, ServerSoftware sw, string mcVersion)
        {
            if (sw.AddonFolder == null) return new List<ModrinthHit>();

            string facets;
            if (sw.AddonFolder == "plugins")
            {
                var cats = new List<string> { "paper", "spigot", "bukkit" };
                if (sw.Id == "purpur") cats.Add("purpur");
                facets = "[[" + string.Join(",", cats.Select(c => "\"categories:" + c + "\"")) + "],[\"versions:" + mcVersion + "\"],[\"project_type:plugin\"]]";
            }
            else
            {
                // Leave out client-only mods (shaders, minimaps...) - they do nothing on a server.
                facets = "[[\"categories:" + sw.ModrinthLoaders[0] + "\"],[\"versions:" + mcVersion + "\"],[\"project_type:mod\"]," +
                         "[\"server_side:required\",\"server_side:optional\"]]";
            }

            var url = "https://api.modrinth.com/v2/search?limit=30" +
                      "&index=" + (string.IsNullOrWhiteSpace(query) ? "downloads" : "relevance") +
                      "&facets=" + Uri.EscapeDataString(facets) +
                      (string.IsNullOrWhiteSpace(query) ? "" : "&query=" + Uri.EscapeDataString(query));
            var d = Json.ParseObject(await Net.GetStringAsync(url).ConfigureAwait(false));
            return d.Arr("hits").OfType<Dictionary<string, object>>().Select(h => new ModrinthHit
            {
                Slug = h.Str("slug") ?? h.Str("project_id"),
                Title = h.Str("title"),
                Description = h.Str("description"),
                Downloads = h.Long("downloads"),
            }).Where(h => h.Slug != null).ToList();
        }
    }
}
