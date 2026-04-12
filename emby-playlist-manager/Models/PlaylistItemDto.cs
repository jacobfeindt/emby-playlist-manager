namespace EmbyPlaylistManager.Models
{
    public class PlaylistItemDto
    {
        public string Name { get; set; }
        public string ImdbId { get; set; }
        public int? TmdbId { get; set; }

        // Episode-specific fields
        public int? SeriesTmdbId { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
    }
}
