using System;

namespace EmbyPlaylistManager
{
    /// <summary>
    /// Lets only one long-running playlist operation (import, repair, scan, sort, the scheduled repair task) run at a time.
    /// They all read and rewrite playlists and shadow files, so overlapping them can undo each other's work.
    /// Acquire it before starting the work and dispose the returned token when the work ends.
    /// </summary>
    public sealed class OperationGate
    {
        private readonly object _sync = new object();
        private string _current;

        /// <summary>Name of the running operation, or null when idle.</summary>
        public string Current
        {
            get { lock (_sync) { return _current; } }
        }

        public bool TryBegin(string operationName, out IDisposable token)
        {
            lock (_sync)
            {
                if (_current != null)
                {
                    token = null;
                    return false;
                }
                _current = operationName;
            }
            token = new Releaser(this);
            return true;
        }

        private void End()
        {
            lock (_sync) { _current = null; }
        }

        private sealed class Releaser : IDisposable
        {
            private OperationGate _owner;

            public Releaser(OperationGate owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                var owner = System.Threading.Interlocked.Exchange(ref _owner, null);
                owner?.End();
            }
        }
    }
}
