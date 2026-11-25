using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;

namespace EmbyPlaylistManager.UI
{
    /// <summary>
    /// Shared plumbing for every plugin page: app/user/library access, the playlist dropdown source, and the
    /// single-operation gate. Each concrete page binds its own options class in <see cref="ContentData"/>.
    /// </summary>
    internal abstract class PluginPageViewBase : UIBaseClasses.Views.PluginPageView
    {
        protected readonly IServerApplicationHost appHost;
        protected readonly IUserManager userManager;
        protected readonly ILibraryManager libraryManager;
        protected readonly ILogger logger;

        private Plugin pluginInstance;
        protected Plugin Plugin => pluginInstance ?? (pluginInstance = appHost.Resolve<Plugin>());

        protected PluginPageViewBase(
            PluginInfo pluginInfo,
            IServerApplicationHost appHost,
            IUserManager userManager,
            ILibraryManager libraryManager,
            ILogger logger)
            : base(pluginInfo.Id)
        {
            this.appHost = appHost;
            this.userManager = userManager;
            this.libraryManager = libraryManager;
            this.logger = logger;
        }

        protected User GetUser()
        {
            return this.User != null
                ? userManager.GetUserById(this.User.Id)
                : userManager.GetUserList(new MediaBrowser.Model.Querying.UserQuery())[0];
        }

        protected List<EditorSelectOption> GetPlaylistChoices()
        {
            var plugin = this.Plugin;
            return plugin != null ? plugin.SortService.ListPlaylists(GetUser()) : new List<EditorSelectOption>();
        }

        protected static string KeepOrFirst(string selected, List<EditorSelectOption> choices)
        {
            bool stillThere = !string.IsNullOrEmpty(selected) &&
                choices.Any(c => string.Equals(Convert.ToString(c.Value), selected, StringComparison.OrdinalIgnoreCase));
            if (stillThere) return selected;
            return choices.Count > 0 ? Convert.ToString(choices[0].Value) : null;
        }

        protected bool TryEnterGate(string operationName, Action<string, ItemStatus> reportStatus, out IDisposable token)
        {
            var plugin = this.Plugin;
            if (plugin != null && plugin.Gate.TryBegin(operationName, out token))
            {
                logger.Info("{0}: started.", operationName);
                return true;
            }

            token = null;
            var running = plugin?.Gate.Current ?? "unknown";
            logger.Warn("{0}: not started, another operation ({1}) is running.", operationName, running);
            reportStatus($"Another operation ({running}) is still running. Try again when it has finished.", ItemStatus.Warning);
            return false;
        }

        protected Task<IPluginUIView> RunGated(string operationName, Func<Task> work, Action<string, ItemStatus> reportStatus)
        {
            if (!TryEnterGate(operationName, reportStatus, out var token))
                return Task.FromResult<IPluginUIView>(this);

            Task.Run(async () =>
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    // Let the page receive this command's response before the first status update is raised.
                    await Task.Delay(300);
                    await work();
                }
                catch (Exception ex) { logger.ErrorException("{0}: unhandled error", ex, operationName); }
                finally
                {
                    token.Dispose();
                    logger.Info("{0}: finished in {1} ms.", operationName, timer.ElapsedMilliseconds);
                }
            });
            return Task.FromResult<IPluginUIView>(this);
        }

        /// <summary>For quick operations: the work runs inside the command, so the command's response already carries the result.</summary>
        protected async Task<IPluginUIView> RunGatedInline(string operationName, Func<Task> work, Action<string, ItemStatus> reportStatus)
        {
            if (!TryEnterGate(operationName, reportStatus, out var token))
                return this;

            var timer = System.Diagnostics.Stopwatch.StartNew();
            try { await work(); }
            catch (Exception ex) { logger.ErrorException("{0}: unhandled error", ex, operationName); }
            finally
            {
                token.Dispose();
                logger.Info("{0}: finished in {1} ms.", operationName, timer.ElapsedMilliseconds);
            }
            return this;
        }
    }
}
