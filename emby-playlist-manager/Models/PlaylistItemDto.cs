using System.Collections.Generic;

namespace EmbyPlaylistManager.Models
{
    public class PlaylistItemDto
    {
        public string Name { get; set; }
        public Dictionary<string, string> ProviderIds { get; set; } = new Dictionary<string, string>();

        // Episode-specific fields
        public int? SeriesTmdbId { get; set; }
        public int? SeriesTvdbId { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
    }
}
