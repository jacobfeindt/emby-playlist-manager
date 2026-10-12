using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

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

        public static PlaylistItemDto FromItem(BaseItem item, User user, ILibraryManager libraryManager)
        {
            var dto = new PlaylistItemDto { Name = item.Name };

            if (item.ProviderIds != null)
                foreach (var kvp in item.ProviderIds)
                    dto.ProviderIds[kvp.Key] = kvp.Value;

            if (item.GetType().Name == "Episode")
            {
                // Try SeriesProviderIds via reflection first
                var seriesProp = item.GetType().GetProperty("SeriesProviderIds");
                var seriesIds = seriesProp?.GetValue(item) as IDictionary<string, string>;
                if (seriesIds != null)
                {
                    if (seriesIds.TryGetValue("Tmdb", out var sTmdb) && int.TryParse(sTmdb, out var sTmdbId))
                        dto.SeriesTmdbId = sTmdbId;
                    if (seriesIds.TryGetValue("Tvdb", out var sTvdb) && int.TryParse(sTvdb, out var sTvdbId))
                        dto.SeriesTvdbId = sTvdbId;
                }

                // Fallback: walk up to the Series item directly
                if (dto.SeriesTmdbId == null && dto.SeriesTvdbId == null)
                {
                    var seriesIdProp = item.GetType().GetProperty("SeriesId");
                    if (seriesIdProp?.GetValue(item) is long seriesInternalId && seriesInternalId > 0)
                    {
                        var seriesItem = libraryManager.GetItemList(new InternalItemsQuery(user)
                        {
                            IncludeItemTypes = new[] { "Series" },
                            AncestorIds = new[] { seriesInternalId },
                            Limit = 1
                        }).FirstOrDefault();

                        if (seriesItem?.ProviderIds != null)
                        {
                            if (seriesItem.ProviderIds.TryGetValue("Tmdb", out var st) && int.TryParse(st, out var stId))
                                dto.SeriesTmdbId = stId;
                            if (seriesItem.ProviderIds.TryGetValue("Tvdb", out var sv) && int.TryParse(sv, out var svId))
                                dto.SeriesTvdbId = svId;
                        }
                    }
                }

                var seasonProp = item.GetType().GetProperty("ParentIndexNumber");
                var indexProp = item.GetType().GetProperty("IndexNumber");
                if (seasonProp != null) dto.Season = (int?)seasonProp.GetValue(item);
                if (indexProp != null) dto.Episode = (int?)indexProp.GetValue(item);
            }

            return dto;
        }
    }
}
