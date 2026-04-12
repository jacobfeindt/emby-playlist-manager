using System;
using Emby.Web.GenericEdit;

namespace EmbyPlaylistManager.UIBaseClasses.Store
{
    public class FileSavingEventArgs : EventArgs
    {
        public FileSavingEventArgs(EditableOptionsBase options) { Options = options; }
        public EditableOptionsBase Options { get; }
        public bool Cancel { get; set; }
    }

    public class FileSavedEventArgs : EventArgs
    {
        public FileSavedEventArgs(EditableOptionsBase options) { Options = options; }
        public EditableOptionsBase Options { get; }
    }
}
