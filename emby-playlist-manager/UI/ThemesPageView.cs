using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using EmbyPlaylistManager.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;

namespace EmbyPlaylistManager.UI
{
    /// <summary>
    /// Manages "themes": named tags applied to collections, so theme lists can be told apart from movie-series box sets.
    /// Emby's box-set libraries cannot be filtered, so the tag (not a virtual library) is the reliable grouping mechanism.
    /// </summary>
    internal class ThemesPageView : PluginPageViewBase
    {
        private readonly OptionsStore<ThemesOptions> store;

        public ThemesPageView(
            PluginInfo pluginInfo,
            IServerApplicationHost appHost,
            IUserManager userManager,
            ILibraryManager libraryManager,
            ILogger logger,
            OptionsStore<ThemesOptions> store)
            : base(pluginInfo, appHost, userManager, libraryManager, logger)
        {
            this.store = store;
            this.ContentData = store.GetOptions();
            RefreshAll();
        }

        private ThemesOptions Options => this.ContentData as ThemesOptions;

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            switch (commandId)
            {
                case "AddTheme": HandleAddTheme(); return Task.FromResult<IPluginUIView>(this);
                case "RemoveTheme": HandleRemoveTheme(); return Task.FromResult<IPluginUIView>(this);
                case "AssignTheme": HandleAssignTheme(); return Task.FromResult<IPluginUIView>(this);
                case "UnassignTheme": HandleUnassignTheme(); return Task.FromResult<IPluginUIView>(this);
                case "Refresh": RefreshAll(); SetStatus("Refreshed.", ItemStatus.Unavailable); return Task.FromResult<IPluginUIView>(this);
                case "ShuffleHome": return HandleShuffleHome();
            }
            return base.RunCommand(itemId, commandId, data);
        }

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            // Clear transient display state before persisting; keep ThemeNames.
            this.Options.ThemeList.Clear();
            this.Options.CollectionList.Clear();
            this.Options.ThemeChoices = new List<EditorSelectOption>();
            this.Options.CollectionChoices = new List<EditorSelectOption>();
            this.Options.NewThemeName = null;
            this.Options.Status.StatusText = "Ready.";
            this.Options.Status.Status = ItemStatus.Unavailable;
            logger.Info("Themes: saving {0} theme names to {1}.", this.Options.ThemeNames.Count, store.OptionsFilePath);
            store.SetOptions(this.Options);
            logger.Info("Themes: saved {0} theme names.", this.Options.ThemeNames.Count);
            RefreshAll();
            return base.OnSaveCommand(itemId, commandId, data);
        }

        // ------------------------------------------------------------------ handlers

        private void HandleAddTheme()
        {
            var name = (this.Options.NewThemeName ?? string.Empty).Trim();
            if (name.Length == 0) { SetStatus("Type a theme name first.", ItemStatus.Warning); return; }
            if (this.Options.ThemeNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                SetStatus($"Theme '{name}' already exists.", ItemStatus.Warning);
                return;
            }

            this.Options.ThemeNames.Add(name);
            this.Options.NewThemeName = null;
            RefreshThemes();
            logger.Info("Themes: added theme '{0}' (now {1} names).", name, this.Options.ThemeNames.Count);
            SetStatus($"Added theme '{name}'. Save to keep it.", ItemStatus.Succeeded);
        }

        private void HandleRemoveTheme()
        {
            var theme = (this.Options.SelectedTheme ?? string.Empty).Trim();
            if (theme.Length == 0) { SetStatus("Choose a theme to remove.", ItemStatus.Warning); return; }

            this.Options.ThemeNames.RemoveAll(t => string.Equals(t, theme, StringComparison.OrdinalIgnoreCase));
            this.Options.SelectedTheme = null;
            RefreshThemes();
            RefreshCollections();
            logger.Info("Themes: removed theme '{0}' (now {1} names).", theme, this.Options.ThemeNames.Count);
            SetStatus($"Removed theme '{theme}'. Save to keep it.", ItemStatus.Succeeded);
        }

        private void HandleAssignTheme()
        {
            if (!TryResolveSelection(out var boxSet, out var theme)) return;

            var tags = boxSet.Tags ?? new string[0];
            if (tags.Contains(theme, StringComparer.OrdinalIgnoreCase))
            {
                SetStatus($"'{boxSet.Name}' is already in theme '{theme}'.", ItemStatus.Warning);
                return;
            }

            if (SaveTags(boxSet, tags.Concat(new[] { theme }).ToArray()))
            {
                logger.Info("Themes: assigned '{0}' to theme '{1}'.", boxSet.Name, theme);
                SetStatus($"Added '{boxSet.Name}' to theme '{theme}'.", ItemStatus.Succeeded);
            }
            else
            {
                SetStatus($"Failed to save the theme tag on '{boxSet.Name}'. Check the log.", ItemStatus.Failed);
            }
            RefreshCollections();
        }

        private void HandleUnassignTheme()
        {
            if (!TryResolveSelection(out var boxSet, out var theme)) return;

            var tags = boxSet.Tags ?? new string[0];
            if (!tags.Contains(theme, StringComparer.OrdinalIgnoreCase))
            {
                SetStatus($"'{boxSet.Name}' is not in theme '{theme}'.", ItemStatus.Warning);
                return;
            }

            if (SaveTags(boxSet, tags.Where(t => !string.Equals(t, theme, StringComparison.OrdinalIgnoreCase)).ToArray()))
            {
                logger.Info("Themes: removed '{0}' from theme '{1}'.", boxSet.Name, theme);
                SetStatus($"Removed '{boxSet.Name}' from theme '{theme}'.", ItemStatus.Succeeded);
            }
            else
            {
                SetStatus($"Failed to update the theme tag on '{boxSet.Name}'. Check the log.", ItemStatus.Failed);
            }
            RefreshCollections();
        }

        /// <summary>Persists a collection's tags. Returns false (and logs) if Emby's update fails, e.g. a null parent.</summary>
        private bool SaveTags(BoxSet boxSet, string[] newTags)
        {
            try
            {
                boxSet.Tags = newTags;
                var parent = boxSet.Parent ?? (boxSet.ParentId != 0 ? libraryManager.GetItemById(boxSet.ParentId) as Folder : null);
                libraryManager.UpdateItem(boxSet, parent, ItemUpdateType.MetadataEdit);
                logger.Info("Themes: saved tags for collection '{0}' ({1}): [{2}]", boxSet.Name, boxSet.InternalId, string.Join(", ", newTags));
                return true;
            }
            catch (Exception ex)
            {
                logger.ErrorException("Themes: failed to save tags for collection '{0}' ({1})", ex, boxSet.Name, boxSet.InternalId);
                return false;
            }
        }

        /// <summary>Resolves the selected collection and theme, reporting a clear problem when either is missing.</summary>
        private bool TryResolveSelection(out BoxSet boxSet, out string theme)
        {
            boxSet = null;
            theme = (this.Options.SelectedTheme ?? string.Empty).Trim();

            if (!Guid.TryParse(this.Options.SelectedCollectionId, out var id))
            {
                SetStatus("Choose a collection first.", ItemStatus.Warning);
                return false;
            }
            if (theme.Length == 0)
            {
                SetStatus("Choose a theme first.", ItemStatus.Warning);
                return false;
            }

            boxSet = libraryManager.GetItemById(id) as BoxSet;
            if (boxSet == null)
            {
                SetStatus("That collection no longer exists.", ItemStatus.Failed);
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ refresh

        private void RefreshAll()
        {
            RefreshThemes();
            RefreshCollections();
        }

        private void RefreshThemes()
        {
            var names = this.Options.ThemeNames;
            this.Options.ThemeChoices = names
                .Select(n => new EditorSelectOption { Value = n, Name = n, IsEnabled = true })
                .ToList();

            this.Options.ThemeList.Clear();
            foreach (var n in names)
            {
                this.Options.ThemeList.Add(new GenericListItem
                {
                    PrimaryText = n,
                    Status = ItemStatus.Succeeded,
                    Icon = IconNames.tag,
                    IconMode = ItemListIconMode.SmallRegular
                });
            }

            if (this.Options.SelectedTheme != null && !names.Contains(this.Options.SelectedTheme, StringComparer.OrdinalIgnoreCase))
                this.Options.SelectedTheme = null;

            RaiseUIViewInfoChanged();
        }

        private void RefreshCollections()
        {
            var user = GetUser();
            var collections = AllCollections(user)
                .Where(c => this.Options.IncludeBoxSetsInAssign || !IsAutoBoxSet(c))
                .ToList();
            var names = this.Options.ThemeNames;

            this.Options.CollectionChoices = collections
                .Select(c => new EditorSelectOption { Value = c.Id.ToString("N"), Name = c.Name, IsEnabled = true })
                .ToList();

            this.Options.CollectionList.Clear();
            foreach (var c in collections)
            {
                var themes = ThemesOf(c, names);
                this.Options.CollectionList.Add(new GenericListItem
                {
                    PrimaryText = c.Name,
                    SecondaryText = themes.Count > 0 ? "Themes: " + string.Join(", ", themes) : "No theme",
                    Status = themes.Count > 0 ? ItemStatus.Succeeded : ItemStatus.Unavailable,
                    Icon = themes.Count > 0 ? IconNames.tag : IconNames.sell,
                    IconMode = ItemListIconMode.SmallRegular
                });
            }
            RaiseUIViewInfoChanged();
        }

        private List<BoxSet> AllCollections(User user)
        {
            return libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { "BoxSet" },
                Recursive = true
            }).OfType<BoxSet>().ToList();
        }

        /// <summary>An auto-created "box set" is a movie-series collection that carries a provider id (TMDb/IMDb/TVDb).</summary>
        private static bool IsAutoBoxSet(BoxSet boxSet)
            => boxSet.ProviderIds != null && boxSet.ProviderIds.Count > 0;

        private static List<string> ThemesOf(BoxSet boxSet, List<string> themeNames)
        {
            var tags = boxSet.Tags ?? new string[0];
            return themeNames.Where(t => tags.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        private async Task<IPluginUIView> HandleShuffleHome()
        {
            SetStatus("Shuffling themes onto the home screen...", ItemStatus.InProgress);
            try
            {
                var count = Math.Max(1, this.Options.HomeThemeCount);
                var shown = await this.Plugin.HomeScreenService.ShuffleThemesOnHome(GetUser(), count, this.Options.ThemeNames, this.Options.IncludeBoxSets, this.Options.RandomizeItemOrder);
                SetStatus($"Showing {shown} theme collections on the home screen.", ItemStatus.Succeeded);
            }
            catch (Exception ex)
            {
                logger.ErrorException("Shuffle themes on home failed", ex);
                SetStatus($"Shuffle failed: {ex.Message}", ItemStatus.Failed);
            }
            return this;
        }

        private void SetStatus(string message, ItemStatus status)
        {
            this.Options.Status.StatusText = message;
            this.Options.Status.Status = status;
            RaiseUIViewInfoChanged();
        }
    }
}
