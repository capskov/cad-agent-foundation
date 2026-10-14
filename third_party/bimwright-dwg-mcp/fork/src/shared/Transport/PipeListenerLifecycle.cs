#nullable enable

using System;

namespace Bimwright.Dwg.Plugin
{
    /// <summary>
    /// Host-free gate for a listener's accept resource. Start generations make
    /// an accept loop from an earlier run unable to publish after Stop/restart.
    /// </summary>
    internal sealed class PipeListenerLifecycle<TResource> where TResource : class, IDisposable
    {
        private readonly object _gate = new object();
        private bool _running;
        private long _generation;
        private TResource? _active;

        public bool IsRunning
        {
            get
            {
                lock (_gate)
                    return _running;
            }
        }

        public TResource? Active
        {
            get
            {
                lock (_gate)
                    return _active;
            }
        }

        public long Start()
        {
            lock (_gate)
            {
                if (_running)
                    throw new InvalidOperationException("The pipe listener is already running.");

                _generation++;
                _running = true;
                return _generation;
            }
        }

        public bool IsCurrent(long generation)
        {
            lock (_gate)
                return _running && generation == _generation;
        }

        /// <summary>Publishes listener metadata only while this run is current.</summary>
        public bool RunIfCurrent(long generation, Action action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            lock (_gate)
            {
                if (!_running || generation != _generation)
                    return false;

                action();
                return _running && generation == _generation;
            }
        }

        /// <summary>
        /// Creates and publishes the accept resource under the same gate used
        /// by Stop. A caller never receives a resource from a stale generation.
        /// </summary>
        public TResource? CreateAndPublish(long generation, Func<TResource> create,
            Action<TResource> publish)
        {
            if (create == null)
                throw new ArgumentNullException(nameof(create));
            if (publish == null)
                throw new ArgumentNullException(nameof(publish));

            lock (_gate)
            {
                if (!_running || generation != _generation)
                    return null;

                TResource resource = create();
                if (!_running || generation != _generation)
                {
                    resource.Dispose();
                    return null;
                }

                _active = resource;
                try
                {
                    publish(resource);
                    return resource;
                }
                catch
                {
                    _active = null;
                    try { resource.Dispose(); } catch { }
                    throw;
                }
            }
        }

        /// <summary>Unpublishes and disposes the resource if it is still current.</summary>
        public void Release(long generation, TResource? resource, Action<TResource> unpublish)
        {
            if (resource == null)
                return;
            if (unpublish == null)
                throw new ArgumentNullException(nameof(unpublish));

            lock (_gate)
            {
                if (generation != _generation || !ReferenceEquals(_active, resource))
                    return;

                _active = null;
                try { unpublish(resource); }
                finally { resource.Dispose(); }
            }
        }

        /// <summary>
        /// Closes admission, invalidates all older accept generations, and
        /// disposes the published resource before a same-name restart can begin.
        /// </summary>
        public void Stop(Action<TResource> unpublish) => Stop(unpublish, delegate { });

        /// <summary>
        /// Stops the current run and performs associated metadata cleanup while
        /// holding the lifecycle gate, serialized with publication and restart.
        /// </summary>
        public void Stop(Action<TResource> unpublish, Action cleanup)
        {
            if (unpublish == null)
                throw new ArgumentNullException(nameof(unpublish));
            if (cleanup == null)
                throw new ArgumentNullException(nameof(cleanup));

            lock (_gate)
            {
                StopLocked(unpublish, cleanup);
            }
        }

        /// <summary>
        /// Stops only the specified run. A late failure from an older Start
        /// cannot stop a newer run or clean its published metadata.
        /// </summary>
        public bool Stop(long generation, Action<TResource> unpublish, Action cleanup)
        {
            if (unpublish == null)
                throw new ArgumentNullException(nameof(unpublish));
            if (cleanup == null)
                throw new ArgumentNullException(nameof(cleanup));

            lock (_gate)
            {
                if (!_running || generation != _generation)
                    return false;

                StopLocked(unpublish, cleanup);
                return true;
            }
        }

        private void StopLocked(Action<TResource> unpublish, Action cleanup)
        {
            _running = false;
            _generation++;
            TResource? resource = _active;
            _active = null;
            try
            {
                if (resource != null)
                {
                    try { unpublish(resource); }
                    finally { resource.Dispose(); }
                }
            }
            finally
            {
                cleanup();
            }
        }
    }
}
