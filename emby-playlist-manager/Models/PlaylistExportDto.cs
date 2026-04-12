using System;
using System.Collections.Generic;

namespace EmbyPlaylistManager.Models
{
    public class PlaylistExportDto
    {
        public string PlaylistName { get; set; }
        public Guid PlaylistId { get; set; }
        public List<PlaylistItemDto> Items { get; set; } = new List<PlaylistItemDto>();
    }
}
