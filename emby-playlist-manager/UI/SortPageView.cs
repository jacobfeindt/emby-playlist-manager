using System;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using EmbyPlaylistManager.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;

namespace EmbyPlaylistManager.UI
{
    internal class SortPageView : PluginPageViewBase
    {
        private readonly OptionsStore<SortOptions> store;

        // The plan produced by the last "Preview Sort". Kept here, not in the options, so it is never persisted.
        private SortPlan sortPlan;

        public SortPageView(
            PluginInfo pluginInfo,
            IServerApplicationHost appHost,
            IUserManager userManager,
            ILibraryManager libraryManager,
            ILogger logger,
            OptionsStore<SortOptions> store)
            : base(pluginInfo, appHost, userManager, libraryManager, logger)
        {
            this.store = store;
            this.ContentData = store.GetOptions();
            RefreshSortChoices();
        }

        private SortOptions Options => this.ContentData as SortOptions;

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            switch (commandId)
            {
                case "SortRefresh":
                    RefreshSortChoices();
                    SetSortStatus("Playlist list refreshed.", ItemStatus.Unavailable);
                    return Task.FromResult<IPluginUIView>(this);
                case "SortChanged":
                    this.sortPlan = null;
                    this.Options.SortPreviewList.Clear();
                    SetSortStatus("Click Preview Sort to see the new order.", ItemStatus.Unavailable);
                    return Task.FromResult<IPluginUIView>(this);
                case "SortPreview":
                    if (!Guid.TryParse(this.Options.SortPlaylistId, out _))
                    {
                        SetSortStatus("Choose a playlist first.", ItemStatus.Warning);
                        return Task.FromResult<IPluginUIView>(this);
                    }
                    return RunGatedInline("Sort preview", HandleSortPreview, SetSortStatus);
                case "SortApply":
                    if (this.sortPlan == null)
                    {
                        SetSortStatus("Click Preview Sort first. Apply only works on a fresh preview.", ItemStatus.Warning);
                        return Task.FromResult<IPluginUIView>(this);
                    }
                    return RunGated("Sort", HandleSortApply, SetSortStatus);
            }
            return base.RunCommand(itemId, commandId, data);
        }

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            this.sortPlan = null;
            this.Options.SortPreviewList.Clear();
            this.Options.SortStatus.StatusText = "Choose a playlist and a sort, then click Preview Sort.";
            this.Options.SortStatus.Status = ItemStatus.Unavailable;
            this.Options.SortApplyButton.IsEnabled = false;
            this.Options.SortPlaylistChoices = new System.Collections.Generic.List<Emby.Web.GenericEdit.Common.EditorSelectOption>();
            store.SetOptions(this.Options);
            RefreshSortChoices();
            return base.OnSaveCommand(itemId, commandId, data);
        }

        private void SetSortStatus(string message, ItemStatus status)
        {
            this.Options.SortStatus.StatusText = message;
            this.Options.SortStatus.Status = status;
            bool enabled = status != ItemStatus.InProgress;
            this.Options.SortRefreshButton.IsEnabled = enabled;
            this.Options.SortPreviewButton.IsEnabled = enabled;
            this.Options.SortApplyButton.IsEnabled = enabled && this.sortPlan != null && this.sortPlan.ChangedPositions > 0;
            RaiseUIViewInfoChanged();
        }

        private void RefreshSortChoices()
        {
            try
            {
                var choices = GetPlaylistChoices();
                this.Options.SortPlaylistChoices = choices;
                this.Options.SortPlaylistId = KeepOrFirst(this.Options.SortPlaylistId, choices);
                logger.Info("Sort: {0} playlists listed for the dropdown; selected: {1}.", choices.Count, this.Options.SortPlaylistId ?? "none");
                RaiseUIViewInfoChanged();
            }
            catch (Exception ex)
            {
                logger.ErrorException("Could not list playlists for sorting", ex);
            }
        }

        private Task HandleSortPreview()
        {
            SetSortStatus("Reading the playlist...", ItemStatus.InProgress);
            this.sortPlan = null;
            var previewTimer = System.Diagnostics.Stopwatch.StartNew();
            logger.Info("Sort preview: started for playlist {0} (sort {1}, direction {2}).",
                this.Options.SortPlaylistId ?? "none", this.Options.SortMode, this.Options.SortDirectionChoice);
            try
            {
                Guid playlistId;
                if (!Guid.TryParse(this.Options.SortPlaylistId, out playlistId))
                {
                    SetSortStatus("Choose a playlist first.", ItemStatus.Warning);
                    return Task.CompletedTask;
                }

                var sorter = this.Plugin.SortService;
                var plan = sorter.BuildPlan(GetUser(), playlistId, this.Options.SortMode, this.Options.SortDirectionChoice);
                logger.Info("Sort preview: '{0}' has {1} items, {2} change position, {3} planned moves{4}. Plan built in {5} ms.",
                    plan.PlaylistName, plan.SnapshotEntryIds.Count, plan.ChangedPositions, plan.Moves.Count, plan.UseRebuild ? " (rebuild)" : string.Empty, previewTimer.ElapsedMilliseconds);

                var list = this.Options.SortPreviewList;
                list.Clear();
                foreach (var row in sorter.BuildPreviewRows(plan))
                {
                    bool moved = row.OldPosition != row.NewPosition;
                    list.Add(new GenericListItem
                    {
                        PrimaryText = $"{row.NewPosition}. {row.Text}",
                        SecondaryText = moved ? $"now at position {row.OldPosition}" : "stays in place",
                        Status = ItemStatus.Succeeded,
                        Icon = moved ? IconNames.info : IconNames.check_circle,
                        IconMode = ItemListIconMode.SmallRegular
                    });
                }
                this.sortPlan = plan;

                var text = plan.DescribeEffect() + (plan.Target.Count > list.Count ? $" Showing the first {list.Count}." : string.Empty);
                logger.Info("Sort preview: showing {0} rows; updating the page.", list.Count);
                SetSortStatus(text, plan.ChangedPositions == 0 ? ItemStatus.Succeeded : ItemStatus.Warning);
                logger.Info("Sort preview: page updated after {0} ms.", previewTimer.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                logger.ErrorException("Sort preview failed", ex);
                this.sortPlan = null;
                SetSortStatus($"Preview failed: {ex.Message}", ItemStatus.Failed);
            }
            return Task.CompletedTask;
        }

        private async Task HandleSortApply()
        {
            var plan = this.sortPlan;
            if (plan == null)
            {
                SetSortStatus("Click Preview Sort first.", ItemStatus.Warning);
                return;
            }

            SetSortStatus("Sorting...", ItemStatus.InProgress);
            try
            {
                var result = await this.Plugin.SortService.Apply(
                    GetUser(),
                    plan,
                    message => SetSortStatus(message, ItemStatus.InProgress),
                    this.Plugin.ShadowStore.GetOptions().EnableShadow,
                    System.Threading.CancellationToken.None);

                this.sortPlan = null;
                this.Options.SortPreviewList.Clear();
                SetSortStatus(result.Message, result.Success ? ItemStatus.Succeeded : ItemStatus.Failed);
            }
            catch (Exception ex)
            {
                logger.ErrorException("Sort failed", ex);
                this.sortPlan = null;
                SetSortStatus($"Sort failed: {ex.Message}", ItemStatus.Failed);
            }
        }
    }
}
