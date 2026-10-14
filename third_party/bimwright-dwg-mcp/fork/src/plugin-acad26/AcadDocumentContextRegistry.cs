using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Bimwright.Dwg.Plugin.Execution;

namespace Bimwright.Dwg.Plugin
{
    /// <summary>
    /// Binds an opaque request context to one exact AutoCAD document lifetime.
    /// Construct and use this registry on AutoCAD's main thread. The canonical
    /// target read method is the only operation intended for worker threads.
    /// </summary>
    public sealed class AcadDocumentContextRegistry : IDisposable
    {
        private readonly object _gate = new object();
        private readonly int _mainThreadId;
        private readonly DocumentCollection _documents;
        private readonly Dictionary<string, BindingEntry> _bindings =
            new Dictionary<string, BindingEntry>(StringComparer.Ordinal);
        private readonly Dictionary<Document, DocumentSession> _sessions =
            new Dictionary<Document, DocumentSession>(ReferenceEqualityComparer.Instance);
        private bool _disposed;

        public AcadDocumentContextRegistry()
        {
            _mainThreadId = Environment.CurrentManagedThreadId;

            try
            {
                _documents = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager;
                if (_documents == null)
                    throw new AcadDocumentContextException(AcadDocumentContextError.DocumentManagerUnavailable);

                _documents.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            }
            catch (AcadDocumentContextException)
            {
                throw;
            }
            catch
            {
                throw new AcadDocumentContextException(AcadDocumentContextError.DocumentManagerUnavailable);
            }
        }

        /// <summary>
        /// Binds only the document active at this explicit call. The returned
        /// target JSON is canonical and contains only identities captured here.
        /// </summary>
        public AcadDocumentContextBinding BindActiveDocument()
        {
            EnsureMainThread();
            EnsureNotDisposed();

            Document document;
            try
            {
                // Deliberately the only MdiActiveDocument access in this type.
                document = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            }
            catch
            {
                throw new AcadDocumentContextException(AcadDocumentContextError.NoActiveDocument);
            }

            if (document == null)
                throw new AcadDocumentContextException(AcadDocumentContextError.NoActiveDocument);

            DrawingIdentity drawing = CaptureDrawingIdentity(document);
            OwnerSnapshot owner = CaptureOwner();
            string contextId = Guid.NewGuid().ToString("N");

            lock (_gate)
            {
                EnsureNotDisposedLocked();

                bool newSession = !_sessions.TryGetValue(document, out DocumentSession session);
                if (newSession)
                {
                    session = new DocumentSession(document, Guid.NewGuid().ToString("N"),
                        Guid.NewGuid().ToString("N"), drawing.NativeDatabaseHandle);
                }
                else if (session.Invalidated)
                    throw new AcadDocumentContextException(AcadDocumentContextError.StaleContext);
                else if (session.NativeDatabaseHandle != drawing.NativeDatabaseHandle)
                {
                    InvalidateSessionBindingsLocked(session);
                    session.NativeDatabaseHandle = drawing.NativeDatabaseHandle;
                    session.DatabaseSessionId = Guid.NewGuid().ToString("N");
                }

                string targetJson = CreateCanonicalTargetJson(contextId, session.SessionId,
                    session.DatabaseSessionId, drawing, owner);
                var entry = new BindingEntry(contextId, targetJson, document, session, drawing,
                    session.DatabaseSessionId, owner);

                if (newSession)
                    _sessions.Add(document, session);
                session.ContextIds.Add(contextId);
                _bindings.Add(contextId, entry);

                return new AcadDocumentContextBinding(contextId, targetJson);
            }
        }

        /// <summary>
        /// Returns the immutable target captured for a context. This method
        /// reads registry memory only and is safe to call from a worker thread.
        /// </summary>
        public string GetCanonicalTargetJson(string contextId)
        {
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(contextId)
                    || !_bindings.TryGetValue(contextId, out BindingEntry entry)
                    || entry.Invalidated
                    || entry.Session.Invalidated)
                {
                    throw new AcadDocumentContextException(AcadDocumentContextError.StaleContext);
                }

                return entry.TargetJson;
            }
        }

        /// <summary>
        /// Returns only the saved path captured by bind. This is a read of the
        /// immutable registry and is safe on the submit transport worker; the
        /// queued write later revalidates the live document under its lock.
        /// </summary>
        public string GetSavedPathForWrite(string contextId)
        {
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(contextId)
                    || !_bindings.TryGetValue(contextId, out BindingEntry entry)
                    || entry.Invalidated
                    || entry.Session.Invalidated
                    || string.IsNullOrWhiteSpace(entry.Drawing.SavedPath))
                {
                    throw new AcadDocumentContextException(AcadDocumentContextError.UnsafeDrawingPath);
                }

                return entry.Drawing.SavedPath;
            }
        }

        /// <summary>
        /// Confirms an exact stored target and re-reads the bound document's
        /// database identity. It never follows the current active document.
        /// </summary>
        public void VerifyExact(string contextId, string completeTargetJson)
        {
            EnsureMainThread();
            BindingEntry entry = ResolveLiveBinding(contextId);

            if (!string.Equals(entry.TargetJson, completeTargetJson, StringComparison.Ordinal))
                throw new AcadDocumentContextException(AcadDocumentContextError.TargetMismatch);

            DrawingIdentity currentDrawing = CaptureDrawingIdentity(entry.Document);
            if (!string.Equals(entry.Drawing.Fingerprint, currentDrawing.Fingerprint, StringComparison.Ordinal)
                || !SamePath(entry.Drawing.SavedPath, currentDrawing.SavedPath)
                || entry.Drawing.NativeDatabaseHandle != currentDrawing.NativeDatabaseHandle
                || entry.DatabaseSessionId != entry.Session.DatabaseSessionId
                || entry.Session.NativeDatabaseHandle != currentDrawing.NativeDatabaseHandle)
            {
                throw new AcadDocumentContextException(AcadDocumentContextError.DatabaseIdentityChanged);
            }

            if (!SameOwner(entry.Owner, CaptureOwner()))
                throw new AcadDocumentContextException(AcadDocumentContextError.OwnerChanged);

            // Recheck after AutoCAD object reads in case the document closed
            // reentrantly while its database identity was being captured.
            if (!ReferenceEquals(entry, ResolveLiveBinding(contextId)))
                throw new AcadDocumentContextException(AcadDocumentContextError.StaleContext);
        }

        /// <summary>
        /// Locks the exact bound document, then verifies its complete identity
        /// again immediately before invoking the supplied action.
        /// </summary>
        public T WithExactDocumentLock<T>(string contextId, string completeTargetJson, Func<Document, T> action)
        {
            EnsureMainThread();
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            BindingEntry entry = ResolveLiveBinding(contextId);
            VerifyExact(contextId, completeTargetJson);

            using (entry.Document.LockDocument())
            {
                VerifyExact(contextId, completeTargetJson);
                return action(entry.Document);
            }
        }

        public void Dispose()
        {
            EnsureMainThread();

            lock (_gate)
            {
                if (_disposed)
                    return;

                InvalidateAllLocked();
                _disposed = true;
            }

            try
            {
                _documents.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
            }
            catch
            {
                throw new AcadDocumentContextException(AcadDocumentContextError.DocumentManagerUnavailable);
            }
        }

        private void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs args)
        {
            // AutoCAD normally raises this event on its main thread. If it does
            // not, discard every binding without touching any AutoCAD object.
            if (Environment.CurrentManagedThreadId != _mainThreadId)
            {
                lock (_gate)
                    InvalidateAllLocked();
                return;
            }

            Document closingDocument;
            try
            {
                closingDocument = args == null ? null : args.Document;
            }
            catch
            {
                lock (_gate)
                    InvalidateAllLocked();
                return;
            }

            if (closingDocument == null)
            {
                lock (_gate)
                    InvalidateAllLocked();
                return;
            }

            lock (_gate)
            {
                if (!_sessions.TryGetValue(closingDocument, out DocumentSession session))
                    return;

                session.Invalidated = true;
                foreach (string contextId in session.ContextIds)
                {
                    if (_bindings.TryGetValue(contextId, out BindingEntry entry))
                    {
                        entry.Invalidated = true;
                        _bindings.Remove(contextId);
                    }
                }

                session.ContextIds.Clear();
                _sessions.Remove(closingDocument);
            }
        }

        private BindingEntry ResolveLiveBinding(string contextId)
        {
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(contextId)
                    || !_bindings.TryGetValue(contextId, out BindingEntry entry)
                    || entry.Invalidated
                    || entry.Session.Invalidated
                    || !ReferenceEquals(entry.Document, entry.Session.Document)
                    || !_sessions.TryGetValue(entry.Document, out DocumentSession currentSession)
                    || !ReferenceEquals(currentSession, entry.Session))
                {
                    throw new AcadDocumentContextException(AcadDocumentContextError.StaleContext);
                }

                EnsureNotDisposedLocked();
                return entry;
            }
        }

        private static DrawingIdentity CaptureDrawingIdentity(Document document)
        {
            Database database;
            try
            {
                database = document == null ? null : document.Database;
            }
            catch
            {
                throw new AcadDocumentContextException(AcadDocumentContextError.FingerprintUnavailable);
            }

            if (database == null)
                throw new AcadDocumentContextException(AcadDocumentContextError.FingerprintUnavailable);

            IntPtr nativeDatabaseHandle;
            try
            {
                nativeDatabaseHandle = database.UnmanagedObject;
            }
            catch
            {
                throw new AcadDocumentContextException(AcadDocumentContextError.NativeDatabaseUnavailable);
            }

            if (nativeDatabaseHandle == IntPtr.Zero)
                throw new AcadDocumentContextException(AcadDocumentContextError.NativeDatabaseUnavailable);

            string fingerprintText;
            try
            {
                fingerprintText = database.FingerprintGuid;
            }
            catch
            {
                throw new AcadDocumentContextException(AcadDocumentContextError.FingerprintUnavailable);
            }

            if (string.IsNullOrWhiteSpace(fingerprintText)
                || !Guid.TryParse(fingerprintText, out Guid fingerprintGuid)
                || fingerprintGuid == Guid.Empty)
            {
                throw new AcadDocumentContextException(AcadDocumentContextError.FingerprintUnavailable);
            }

            string filename;
            try
            {
                filename = database.Filename;
            }
            catch
            {
                throw new AcadDocumentContextException(AcadDocumentContextError.UnsafeDrawingPath);
            }

            return new DrawingIdentity(fingerprintGuid.ToString("D"), NormalizeSavedPath(filename),
                nativeDatabaseHandle);
        }

        private static string NormalizeSavedPath(string filename)
        {
            // An empty Database.Filename identifies an unsaved drawing. Its
            // session GUID, carried separately, is the only accepted identity.
            if (string.IsNullOrEmpty(filename))
                return null;

            try
            {
                if (!string.Equals(filename, filename.Trim(), StringComparison.Ordinal)
                    || !Path.IsPathFullyQualified(filename)
                    || filename.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase)
                    || filename.StartsWith("\\\\.\\", StringComparison.OrdinalIgnoreCase))
                {
                    throw new AcadDocumentContextException(AcadDocumentContextError.UnsafeDrawingPath);
                }

                string fullPath = Path.GetFullPath(filename);
                string root = Path.GetPathRoot(fullPath);
                if (!Path.IsPathFullyQualified(fullPath)
                    || string.IsNullOrWhiteSpace(root)
                    || Path.EndsInDirectorySeparator(fullPath)
                    || string.Equals(Path.TrimEndingDirectorySeparator(fullPath),
                        Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
                {
                    throw new AcadDocumentContextException(AcadDocumentContextError.UnsafeDrawingPath);
                }

                return fullPath;
            }
            catch (AcadDocumentContextException)
            {
                throw;
            }
            catch
            {
                throw new AcadDocumentContextException(AcadDocumentContextError.UnsafeDrawingPath);
            }
        }

        private static OwnerSnapshot CaptureOwner()
        {
            PluginOwnerIdentity owner;
            try
            {
                owner = App.PluginOwner;
            }
            catch
            {
                throw new AcadDocumentContextException(AcadDocumentContextError.OwnerUnavailable);
            }

            if (owner == null
                || owner.ProcessId <= 0
                || owner.ProcessStartedAtUtc == default(DateTimeOffset)
                || string.IsNullOrWhiteSpace(owner.PluginGeneration)
                || !Guid.TryParse(owner.OwnerId, out Guid ownerGuid)
                || ownerGuid == Guid.Empty)
            {
                throw new AcadDocumentContextException(AcadDocumentContextError.OwnerUnavailable);
            }

            return new OwnerSnapshot(
                ownerGuid.ToString("D"),
                owner.ProcessId,
                owner.ProcessStartedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                owner.PluginGeneration);
        }

        private static string CreateCanonicalTargetJson(string contextId, string sessionId,
            string databaseSessionId, DrawingIdentity drawing, OwnerSnapshot owner)
        {
            return Acad26DocumentTargetJson.Create(contextId, sessionId, databaseSessionId,
                drawing.Fingerprint, drawing.SavedPath, owner.OwnerId, owner.ProcessId,
                owner.ProcessStartedAtUtc, owner.Generation);
        }

        private static bool SamePath(string left, string right)
        {
            if (left == null || right == null)
                return left == null && right == null;

            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameOwner(OwnerSnapshot left, OwnerSnapshot right)
        {
            return left != null
                && right != null
                && string.Equals(left.OwnerId, right.OwnerId, StringComparison.Ordinal)
                && left.ProcessId == right.ProcessId
                && string.Equals(left.ProcessStartedAtUtc, right.ProcessStartedAtUtc, StringComparison.Ordinal)
                && string.Equals(left.Generation, right.Generation, StringComparison.Ordinal);
        }

        private void InvalidateSessionBindingsLocked(DocumentSession session)
        {
            session.Invalidated = true;
            foreach (string contextId in session.ContextIds)
            {
                if (_bindings.TryGetValue(contextId, out BindingEntry entry))
                {
                    entry.Invalidated = true;
                    _bindings.Remove(contextId);
                }
            }

            session.ContextIds.Clear();
            session.Invalidated = false;
        }

        private void EnsureMainThread()
        {
            if (Environment.CurrentManagedThreadId != _mainThreadId)
                throw new AcadDocumentContextException(AcadDocumentContextError.WrongThread);
        }

        private void EnsureNotDisposed()
        {
            lock (_gate)
                EnsureNotDisposedLocked();
        }

        private void EnsureNotDisposedLocked()
        {
            if (_disposed)
                throw new AcadDocumentContextException(AcadDocumentContextError.Disposed);
        }

        private void InvalidateAllLocked()
        {
            foreach (BindingEntry entry in _bindings.Values)
                entry.Invalidated = true;
            foreach (DocumentSession session in _sessions.Values)
            {
                session.Invalidated = true;
                session.ContextIds.Clear();
            }

            _bindings.Clear();
            _sessions.Clear();
        }

        private sealed class BindingEntry
        {
            public BindingEntry(string contextId, string targetJson, Document document,
                DocumentSession session, DrawingIdentity drawing, string databaseSessionId,
                OwnerSnapshot owner)
            {
                ContextId = contextId;
                TargetJson = targetJson;
                Document = document;
                Session = session;
                Drawing = drawing;
                DatabaseSessionId = databaseSessionId;
                Owner = owner;
            }

            public string ContextId { get; }
            public string TargetJson { get; }
            public Document Document { get; }
            public DocumentSession Session { get; }
            public DrawingIdentity Drawing { get; }
            public string DatabaseSessionId { get; }
            public OwnerSnapshot Owner { get; }
            public bool Invalidated { get; set; }
        }

        private sealed class DocumentSession
        {
            public DocumentSession(Document document, string sessionId, string databaseSessionId,
                IntPtr nativeDatabaseHandle)
            {
                Document = document;
                SessionId = sessionId;
                DatabaseSessionId = databaseSessionId;
                NativeDatabaseHandle = nativeDatabaseHandle;
            }

            public Document Document { get; }
            public string SessionId { get; }
            public string DatabaseSessionId { get; set; }
            public IntPtr NativeDatabaseHandle { get; set; }
            public HashSet<string> ContextIds { get; } = new HashSet<string>(StringComparer.Ordinal);
            public bool Invalidated { get; set; }
        }

        private sealed class DrawingIdentity
        {
            public DrawingIdentity(string fingerprint, string savedPath, IntPtr nativeDatabaseHandle)
            {
                Fingerprint = fingerprint;
                SavedPath = savedPath;
                NativeDatabaseHandle = nativeDatabaseHandle;
            }

            public string Fingerprint { get; }
            public string SavedPath { get; }
            public IntPtr NativeDatabaseHandle { get; }
        }

        private sealed class OwnerSnapshot
        {
            public OwnerSnapshot(string ownerId, int processId, string processStartedAtUtc, string generation)
            {
                OwnerId = ownerId;
                ProcessId = processId;
                ProcessStartedAtUtc = processStartedAtUtc;
                Generation = generation;
            }

            public string OwnerId { get; }
            public int ProcessId { get; }
            public string ProcessStartedAtUtc { get; }
            public string Generation { get; }
        }
    }

    public readonly struct AcadDocumentContextBinding
    {
        public AcadDocumentContextBinding(string contextId, string completeTargetJson)
        {
            ContextId = contextId;
            CompleteTargetJson = completeTargetJson;
        }

        public string ContextId { get; }
        public string CompleteTargetJson { get; }
    }

    public enum AcadDocumentContextError
    {
        WrongThread,
        DocumentManagerUnavailable,
        NoActiveDocument,
        OwnerUnavailable,
        FingerprintUnavailable,
        NativeDatabaseUnavailable,
        UnsafeDrawingPath,
        StaleContext,
        TargetMismatch,
        DocumentIdentityChanged,
        DatabaseIdentityChanged,
        OwnerChanged,
        Disposed
    }

    public sealed class AcadDocumentContextException : InvalidOperationException
    {
        public AcadDocumentContextException(AcadDocumentContextError error)
            : base(GetStableMessage(error))
        {
            Error = error;
        }

        public AcadDocumentContextError Error { get; }

        private static string GetStableMessage(AcadDocumentContextError error)
        {
            switch (error)
            {
                case AcadDocumentContextError.WrongThread:
                    return "AutoCAD document context operations must run on the captured main thread.";
                case AcadDocumentContextError.DocumentManagerUnavailable:
                    return "The AutoCAD document manager is unavailable.";
                case AcadDocumentContextError.NoActiveDocument:
                    return "No active AutoCAD document is available.";
                case AcadDocumentContextError.OwnerUnavailable:
                    return "The AutoCAD process owner identity is unavailable.";
                case AcadDocumentContextError.FingerprintUnavailable:
                    return "The drawing database fingerprint is unavailable or invalid.";
                case AcadDocumentContextError.NativeDatabaseUnavailable:
                    return "The drawing database native identity is unavailable.";
                case AcadDocumentContextError.UnsafeDrawingPath:
                    return "The drawing path cannot be safely normalized.";
                case AcadDocumentContextError.StaleContext:
                    return "The document context is stale or unknown.";
                case AcadDocumentContextError.TargetMismatch:
                    return "The supplied target does not exactly match the bound document context.";
                case AcadDocumentContextError.DocumentIdentityChanged:
                    return "The bound document database identity changed.";
                case AcadDocumentContextError.DatabaseIdentityChanged:
                    return "The bound drawing database identity changed.";
                case AcadDocumentContextError.OwnerChanged:
                    return "The AutoCAD process owner identity changed.";
                case AcadDocumentContextError.Disposed:
                    return "The document context registry has been disposed.";
                default:
                    return "The AutoCAD document context operation failed.";
            }
        }
    }
}
