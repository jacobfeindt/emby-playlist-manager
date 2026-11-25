using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Security;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;

namespace EmbyPlaylistManager
{
    /// <summary>A home-screen section, matching Emby's <c>/Users/{Id}/HomeSections</c> JSON shape.</summary>
    public sealed class HomeSectionDto
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string SectionType { get; set; }
        public string ParentId { get; set; }
        public string[] Monitor { get; set; } = new string[0];
        public string[] ItemTypes { get; set; } = new string[0];
        public string[] ExcludedFolders { get; set; } = new string[0];
        public int CardSizeOffset { get; set; }
        public bool IncludeNextUpInResume { get; set; } = true;
    }

    /// <summary>
    /// Pins a random selection of theme collections onto the user's home screen as "boxset" sections (Netflix-style rows).
    /// Emby has no server-side plugin API to write home sections, so this calls the REST endpoint directly.
    /// </summary>
    public sealed class HomeScreenService
    {
        private readonly IServerApplicationHost _appHost;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;

        public HomeScreenService(IServerApplicationHost appHost, ILibraryManager libraryManager, ILogger logger)
        {
            _appHost = appHost;
            _libraryManager = libraryManager;
            _logger = logger;
        }

        /// <summary>
        /// Collections eligible for the home-screen shuffle: themed collections (tagged with a managed theme name or the
        /// universal marker), plus movie-series box sets when <paramref name="includeBoxSets"/> is true.
        /// </summary>
        public List<BoxSet> GetShufflePool(User user, List<string> themeNames, bool includeBoxSets)
        {
            var all = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { "BoxSet" },
                Recursive = true
            }).OfType<BoxSet>().ToList();

            var tags = new HashSet<string>(themeNames ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            tags.Add(PlaylistConvertService.ThemeCollectionTag);

            return all.Where(b =>
            {
                var boxTags = b.Tags ?? new string[0];
                bool isTheme = boxTags.Any(t => tags.Contains(t));
                bool isBoxSet = b.ProviderIds != null && b.ProviderIds.Count > 0;
                return isTheme || (includeBoxSets && isBoxSet);
            }).ToList();
        }

        /// <summary>
        /// Replaces the shuffle-pool rows on the home screen with a fresh random selection of at most <paramref name="count"/>.
        /// Returns how many rows were shown.
        /// </summary>
        public async Task<int> ShuffleThemesOnHome(User user, int count, List<string> themeNames, bool includeBoxSets, bool randomizeItemOrder)
        {
            var pool = GetShufflePool(user, themeNames, includeBoxSets);
            if (pool.Count == 0)
                throw new InvalidOperationException("No collections to shuffle. Add themes on the Themes page, convert a playlist, or enable 'Include box sets'.");

            var rng = new Random();
            var chosen = pool.OrderBy(_ => rng.Next()).Take(Math.Max(1, count)).ToList();

            long userId = user.InternalId;
            var token = GetCurrentUserToken(user);
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Could not determine the current user's authentication token.");

            var baseUrl = await _appHost.GetLocalApiUrl(System.Threading.CancellationToken.None);
            _logger.Info("Themes on home: shuffling {0} of {1} collections for user '{2}' via {3}.", chosen.Count, pool.Count, user.Name, baseUrl);

            var poolIds = new HashSet<string>(pool.Select(t => t.InternalId.ToString()));
            var current = await GetHomeSections(baseUrl, userId, token);

            // Remove the previous shuffle rows (boxset sections whose parent is in the pool), keep everything else.
            var toDelete = current.Where(s => s.SectionType == "boxset" && poolIds.Contains(s.ParentId)).ToList();
            _logger.Info("Themes on home: {0} existing sections, {1} are ours to replace.", current.Count, toDelete.Count);
            foreach (var s in toDelete)
                await DeleteHomeSection(baseUrl, userId, s.Id, token);

            foreach (var c in chosen)
            {
                if (randomizeItemOrder) SetRandomOrder(c);
                await AddHomeSection(baseUrl, userId, c.InternalId.ToString(), token);
            }

            _logger.Info("Themes on home: done, showing {0} of {1} collections.", chosen.Count, pool.Count);
            return chosen.Count;
        }

        /// <summary>Sets a collection's display order to random so its items shuffle on each view.</summary>
        private void SetRandomOrder(BoxSet boxSet)
        {
            try
            {
                boxSet.SetDisplayOrder(ItemSortBy.Random);
                var parent = boxSet.Parent ?? (boxSet.ParentId != 0 ? _libraryManager.GetItemById(boxSet.ParentId) as Folder : null);
                _libraryManager.UpdateItem(boxSet, parent, ItemUpdateType.MetadataEdit);
                _logger.Info("Themes on home: set '{0}' to random item order.", boxSet.Name);
            }
            catch (Exception ex)
            {
                _logger.Warn("Themes on home: could not randomize '{0}': {1}", boxSet.Name, ex.Message);
            }
        }

        private string GetCurrentUserToken(User user)
        {
            try
            {
                var repo = _appHost.Resolve<IAuthenticationRepository>();
                var result = repo.Get(new AuthenticationInfoQuery
                {
                    UserId = user.InternalId,
                    IsActive = true,
                    Limit = 20
                });
                var info = result.Items
                    ?.OrderByDescending(i => i.DateLastActivity)
                    .FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.AccessToken));
                return info?.AccessToken;
            }
            catch (Exception ex)
            {
                _logger.Warn("Could not resolve the current user's auth token: {0}", ex.Message);
                return null;
            }
        }

        private static async Task<List<HomeSectionDto>> GetHomeSections(string baseUrl, long userId, string token)
        {
            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.Add("X-Emby-Token", token);
                var json = await http.GetStringAsync($"{baseUrl}/Users/{userId}/HomeSections");
                return JsonSerializer.Deserialize<List<HomeSectionDto>>(json) ?? new List<HomeSectionDto>();
            }
        }

        private static async Task AddHomeSection(string baseUrl, long userId, string collectionInternalId, string token)
        {
            var section = new HomeSectionDto
            {
                SectionType = "boxset",
                ParentId = collectionInternalId,
                IncludeNextUpInResume = true
            };
            var json = JsonSerializer.Serialize(section);
            using (var http = new HttpClient())
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            {
                http.DefaultRequestHeaders.Add("X-Emby-Token", token);
                var resp = await http.PostAsync($"{baseUrl}/Users/{userId}/HomeSections", content);
                resp.EnsureSuccessStatusCode();
            }
        }

        private static async Task DeleteHomeSection(string baseUrl, long userId, string sectionId, string token)
        {
            var json = JsonSerializer.Serialize(new { ids = new[] { sectionId } });
            using (var http = new HttpClient())
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            {
                http.DefaultRequestHeaders.Add("X-Emby-Token", token);
                var resp = await http.PostAsync($"{baseUrl}/Users/{userId}/HomeSections/Delete", content);
                resp.EnsureSuccessStatusCode();
            }
        }
    }
}
