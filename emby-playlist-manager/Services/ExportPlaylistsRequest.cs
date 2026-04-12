using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace EmbyPlaylistManager.Services
{
    [Route("/PlaylistManager/Export", "GET", Summary = "Exports all playlists to a JSON file")]
    [Authenticated(Roles = "Admin")]
    public class ExportPlaylistsRequest : IReturn<ExportPlaylistsResponse>
    {
        [ApiMember(Name = "OutputFolder", Description = "Folder to write the export file. Defaults to plugin config folder.", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "GET")]
        public string OutputFolder { get; set; }
    }

    public class ExportPlaylistsResponse
    {
        public string FilePath { get; set; }
        public int PlaylistCount { get; set; }
    }
}
