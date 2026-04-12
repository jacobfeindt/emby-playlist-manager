using System.Collections.Generic;
using EmbyPlaylistManager.Models;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace EmbyPlaylistManager.Services
{
    [Route("/PlaylistManager/Import", "POST", Summary = "Imports playlists from a JSON body")]
    [Authenticated(Roles = "Admin")]
    public class ImportPlaylistsRequest : IReturn<ImportPlaylistsResponse>
    {
        public List<PlaylistExportDto> Playlists { get; set; }
    }

    public class ImportPlaylistsResponse
    {
        public int Imported { get; set; }
        public int Skipped { get; set; }
    }
}
