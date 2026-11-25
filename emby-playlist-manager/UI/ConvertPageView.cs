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
    internal class ConvertPageView : PluginPageViewBase
    {
        private readonly OptionsStore<ConvertOptions> store;

        // The plan produced by the last "Preview Conversion". Kept here, not in the options, so it is never persisted.
        private ConvertPlan convertPlan;

        public ConvertPageView(
            PluginInfo pluginInfo,
            IServerApplicationHost appHost,
            IUserManager userManager,
            ILibraryManager libraryManager,
            ILogger logger,
            OptionsStore<ConvertOptions> store)
            : base(pluginInfo, appHost, userManager, libraryManager, logger)
        {
            this.store = store;
            this.ContentData = store.GetOptions();
            RefreshChoices();
        }

        private ConvertOptions Options => this.ContentData as ConvertOptions;

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            switch (commandId)
            {
                case "ConvertPlaylistChanged":
                    this.Options.ConvertCollectionName = null;
                    ResetConvert("Click Preview Conversion to see what will be added.");
                    return Task.FromResult<IPluginUIView>(this);
                case "ConvertChanged":
                    ResetConvert("Click Preview Conversion to see what will be added.");
                    return Task.FromResult<IPluginUIView>(this);
                case "ConvertPreview":
                    if (!Guid.TryParse(this.Options.ConvertPlaylistId, out _))
                    {
                        SetConvertStatus("Choose a playlist first.", ItemStatus.Warning);
                        return Task.FromResult<IPluginUIView>(this);
                    }
                    return RunGatedInline("Convert preview", HandleConvertPreview, SetConvertStatus);
                case "ConvertApply":
                    if (this.convertPlan == null)
                    {
                        SetConvertStatus("Click Preview Conversion first. Create Collection only works on a fresh preview.", ItemStatus.Warning);
                        return Task.FromResult<IPluginUIView>(this);
                    }
                    return RunGated("Convert", HandleConvertApply, SetConvertStatus);
            }
            return base.RunCommand(itemId, commandId, data);
        }

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            this.convertPlan = null;
            this.Options.ConvertPreviewList.Clear();
            this.Options.ConvertCollectionName = null;
            this.Options.ConvertStatus.StatusText = "Choose a playlist, then click Preview Conversion.";
            this.Options.ConvertStatus.Status = ItemStatus.Unavailable;
            this.Options.ConvertApplyButton.IsEnabled = false;
            this.Options.PlaylistChoices = new System.Collections.Generic.List<Emby.Web.GenericEdit.Common.EditorSelectOption>();
            store.SetOptions(this.Options);
            RefreshChoices();
            return base.OnSaveCommand(itemId, commandId, data);
        }

        private void SetConvertStatus(string message, ItemStatus status)
        {
            this.Options.ConvertStatus.StatusText = message;
            this.Options.ConvertStatus.Status = status;
            bool enabled = status != ItemStatus.InProgress;
            this.Options.ConvertPreviewButton.IsEnabled = enabled;
            this.Options.ConvertApplyButton.IsEnabled = enabled && this.convertPlan != null && this.convertPlan.Problem == null && this.convertPlan.ItemsToAdd.Count > 0;
            RaiseUIViewInfoChanged();
        }

        private void ResetConvert(string message)
        {
            this.convertPlan = null;
            this.Options.ConvertPreviewList.Clear();
            SetConvertStatus(message, ItemStatus.Unavailable);
        }

        private void RefreshChoices()
        {
            try
            {
                var choices = GetPlaylistChoices();
                this.Options.PlaylistChoices = choices;
                this.Options.ConvertPlaylistId = KeepOrFirst(this.Options.ConvertPlaylistId, choices);
                logger.Info("Convert: {0} playlists listed for the dropdown; selected: {1}.", choices.Count, this.Options.ConvertPlaylistId ?? "none");
                RaiseUIViewInfoChanged();
            }
            catch (Exception ex)
            {
                logger.ErrorException("Could not list playlists for conversion", ex);
            }
        }

        private Task HandleConvertPreview()
        {
            SetConvertStatus("Reading the playlist...", ItemStatus.InProgress);
            this.convertPlan = null;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            logger.Info("Convert preview: started for playlist {0}.", this.Options.ConvertPlaylistId ?? "none");
            try
            {
                Guid playlistId;
                if (!Guid.TryParse(this.Options.ConvertPlaylistId, out playlistId))
                {
                    SetConvertStatus("Choose a playlist first.", ItemStatus.Warning);
                    return Task.CompletedTask;
                }

                var service = this.Plugin.ConvertService;
                var plan = service.BuildPlan(GetUser(), playlistId, this.Options.ConvertCollectionName);

                var list = this.Options.ConvertPreviewList;
                list.Clear();
                foreach (var row in service.BuildPreviewRows(plan))
                {
                    list.Add(new GenericListItem
                    {
                        PrimaryText = row.Text,
                        SecondaryText = row.Detail,
                        Status = plan.Problem != null && row.IsSummary ? ItemStatus.Warning : ItemStatus.Succeeded,
                        Icon = row.IsSummary ? IconNames.info : IconNames.check_circle,
                        IconMode = ItemListIconMode.SmallRegular
                    });
                }
                this.convertPlan = plan;

                logger.Info("Convert preview: showing {0} rows; updating the page.", list.Count);
                var done = plan.Problem == null && plan.ItemsToAdd.Count == 0;
                SetConvertStatus(plan.DescribeEffect(), plan.Problem != null ? ItemStatus.Warning : (done ? ItemStatus.Succeeded : ItemStatus.Warning));
                logger.Info("Convert preview: page updated after {0} ms.", timer.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                logger.ErrorException("Convert preview failed", ex);
                this.convertPlan = null;
                SetConvertStatus($"Preview failed: {ex.Message}", ItemStatus.Failed);
            }
            return Task.CompletedTask;
        }

        private async Task HandleConvertApply()
        {
            var plan = this.convertPlan;
            if (plan == null)
            {
                SetConvertStatus("Click Preview Conversion first.", ItemStatus.Warning);
                return;
            }

            SetConvertStatus("Converting...", ItemStatus.InProgress);
            try
            {
                var result = await this.Plugin.ConvertService.Apply(GetUser(), plan, message => SetConvertStatus(message, ItemStatus.InProgress));

                this.convertPlan = null;
                this.Options.ConvertPreviewList.Clear();
                SetConvertStatus(result.Message, result.Success ? (result.NotAdded > 0 ? ItemStatus.Warning : ItemStatus.Succeeded) : ItemStatus.Failed);
            }
            catch (Exception ex)
            {
                logger.ErrorException("Convert failed", ex);
                this.convertPlan = null;
                SetConvertStatus($"Conversion failed: {ex.Message}", ItemStatus.Failed);
            }
        }
    }
}
