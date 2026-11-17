using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Server.Execution
{
    /// <summary>
    /// Durable, single-server ownership for bound plugin contexts and requests.
    /// This is intentionally separate from RequestLedger: this store pins the
    /// original endpoint and grants query rights; it does not infer plugin state.
    /// </summary>
    public sealed class SqliteBoundPluginOwnershipStore : IDisposable
    {
        private const int SchemaVersion = 2;
        private const int BusyTimeoutMilliseconds = 3000;
        private readonly string _databasePath;
        private readonly string _connectionString;
        private readonly FileStream _ownerLock;
        private bool _disposed;

        private SqliteBoundPluginOwnershipStore(string databasePath, FileStream ownerLock)
        {
            _databasePath = databasePath;
            _ownerLock = ownerLock;
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = 3
            };
            _connectionString = builder.ToString();
            InitializeAndRecover();
        }

        public string DatabasePath => _databasePath;

        public static SqliteBoundPluginOwnershipStore Open(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("A durable ownership database path is required.", nameof(databasePath));

            string fullPath = Path.GetFullPath(databasePath);
            string directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("The ownership database must have a parent directory.");
            Directory.CreateDirectory(directory);

            // Keep this file permanently. Removing it after close introduces an
            // ABA race where two owners can lock different file objects.
            FileStream ownerLock = new FileStream(fullPath + ".owner.lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
            try
            {
                return new SqliteBoundPluginOwnershipStore(fullPath, ownerLock);
            }
            catch
            {
                ownerLock.Dispose();
                throw;
            }
        }

        internal IReadOnlyList<BoundPluginContext> LoadContexts()
        {
            ThrowIfDisposed();
            var result = new List<BoundPluginContext>();
            using (SqliteConnection connection = OpenConnection())
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT context_id,complete_target_json,endpoint_json,journal_scope_id,journal_path_digest
FROM bound_contexts ORDER BY context_id COLLATE BINARY;";
                using (SqliteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string contextId = reader.GetString(0);
                        string targetJson = reader.GetString(1);
                        DiscoveryInfo endpoint = DeserializeEndpoint(reader.GetString(2));
                        string scopeId = reader.IsDBNull(3) ? null : reader.GetString(3);
                        string pathDigest = reader.IsDBNull(4) ? null : reader.GetString(4);
                        var context = new BoundPluginContext(contextId, targetJson, endpoint, scopeId, pathDigest);
                        if (!context.TryGetOwnerIdentity(out _, out string error))
                            throw new InvalidDataException("Stored bound context failed identity validation: " + error);
                        result.Add(context);
                    }
                }
            }
            return result;
        }

        internal IReadOnlyList<BoundPluginRequestOwnership> LoadRequests()
        {
            ThrowIfDisposed();
            var result = new List<BoundPluginRequestOwnership>();
            using (SqliteConnection connection = OpenConnection())
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT request_id,context_id,tool_name,canonical_parameters_json,payload_digest,complete_target_json,
 owner_id,provider_generation,state,recovery_query_only,created_at_utc,updated_at_utc,
 plugin_sequence,snapshot_digest,is_write,risk_keys_json,journal_scope_id,journal_path_digest,payload_version
FROM request_owners ORDER BY request_id COLLATE BINARY;";
                using (SqliteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var item = new BoundPluginRequestOwnership(
                            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                            ParseState(reader.GetString(8)), reader.GetInt64(9) != 0,
                            ParseTimestamp(reader.GetString(10)), ParseTimestamp(reader.GetString(11)),
                            reader.IsDBNull(12) ? (long?)null : reader.GetInt64(12),
                            reader.IsDBNull(13) ? null : reader.GetString(13),
                            reader.GetInt64(14) != 0, DeserializeRiskKeys(reader.GetString(15)),
                            reader.IsDBNull(16) ? null : reader.GetString(16),
                            reader.IsDBNull(17) ? null : reader.GetString(17),
                            reader.GetInt32(18));
                        ValidateOwnershipShape(item);
                        if (!string.Equals(item.PayloadDigest, ComputePayloadDigest(item), StringComparison.Ordinal))
                            throw new InvalidDataException("Stored request ownership digest does not match its immutable payload.");
                        result.Add(item);
                    }
                }
            }
            return result;
        }

        internal bool AddContext(BoundPluginContext context, out string error)
        {
            ThrowIfDisposed();
            error = null;
            if (context == null || !context.TryGetOwnerIdentity(out _, out error))
                return false;
            if (!context.TryGetJournalScopeIdentity(required: false, out string journalScopeId,
                out string journalPathDigest, out error))
                return false;

            string endpointJson = SerializeEndpoint(context.Discovery);
            using (SqliteConnection connection = OpenConnection())
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                using (SqliteCommand insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = @"
INSERT OR IGNORE INTO bound_contexts(context_id,complete_target_json,endpoint_json,created_at_utc,
 journal_scope_id,journal_path_digest)
VALUES($id,$target,$endpoint,$created,$scope,$path_digest);";
                    insert.Parameters.AddWithValue("$id", context.ContextId);
                    insert.Parameters.AddWithValue("$target", context.CompleteTargetJson);
                    insert.Parameters.AddWithValue("$endpoint", endpointJson);
                    insert.Parameters.AddWithValue("$created", FormatTimestamp(DateTimeOffset.UtcNow));
                    insert.Parameters.AddWithValue("$scope", (object)journalScopeId ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$path_digest", (object)journalPathDigest ?? DBNull.Value);
                    insert.ExecuteNonQuery();
                }

                using (SqliteCommand read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText = @"SELECT complete_target_json,endpoint_json,journal_scope_id,journal_path_digest
FROM bound_contexts WHERE context_id=$id;";
                    read.Parameters.AddWithValue("$id", context.ContextId);
                    using (SqliteDataReader reader = read.ExecuteReader())
                    {
                        if (!reader.Read()
                            || !string.Equals(reader.GetString(0), context.CompleteTargetJson, StringComparison.Ordinal)
                            || !string.Equals(reader.GetString(1), endpointJson, StringComparison.Ordinal)
                            || !string.Equals(reader.IsDBNull(2) ? null : reader.GetString(2), journalScopeId, StringComparison.Ordinal)
                            || !string.Equals(reader.IsDBNull(3) ? null : reader.GetString(3), journalPathDigest, StringComparison.Ordinal))
                        {
                            error = "context_id is already bound to a different plugin endpoint or target";
                            transaction.Rollback();
                            return false;
                        }
                    }
                }
                transaction.Commit();
                return true;
            }
        }

        internal bool TryReserve(BoundPluginRequestOwnership request, out bool existing, out string error)
        {
            ThrowIfDisposed();
            existing = false;
            error = null;
            if (request == null)
            {
                error = "request ownership is required";
                return false;
            }
            if (request.PayloadVersion != 2
                || !string.Equals(request.PayloadDigest, ComputePayloadDigest(request), StringComparison.Ordinal))
            {
                error = "request ownership immutable payload digest is invalid";
                return false;
            }
            try
            {
                ValidateOwnershipShape(request);
            }
            catch (InvalidDataException exception)
            {
                error = exception.Message;
                return false;
            }

            using (SqliteConnection connection = OpenConnection())
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                using (SqliteCommand read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText = @"
SELECT context_id,tool_name,canonical_parameters_json,payload_digest,complete_target_json,
 owner_id,provider_generation,recovery_query_only,is_write,risk_keys_json,
 journal_scope_id,journal_path_digest,payload_version
FROM request_owners WHERE request_id=$id;";
                    read.Parameters.AddWithValue("$id", request.RequestId);
                    using (SqliteDataReader reader = read.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            existing = true;
                            bool same = string.Equals(reader.GetString(0), request.ContextId, StringComparison.Ordinal)
                                && string.Equals(reader.GetString(1), request.ToolName, StringComparison.Ordinal)
                                && string.Equals(reader.GetString(2), request.CanonicalParametersJson, StringComparison.Ordinal)
                                && string.Equals(reader.GetString(3), request.PayloadDigest, StringComparison.Ordinal)
                                && string.Equals(reader.GetString(4), request.CompleteTargetJson, StringComparison.Ordinal)
                                && string.Equals(reader.GetString(5), request.OwnerId, StringComparison.Ordinal)
                                && string.Equals(reader.GetString(6), request.ProviderGeneration, StringComparison.Ordinal)
                                && (reader.GetInt64(8) != 0) == request.IsWrite
                                && string.Equals(reader.GetString(9), JsonConvert.SerializeObject(request.RiskKeys), StringComparison.Ordinal)
                                && string.Equals(reader.IsDBNull(10) ? null : reader.GetString(10), request.JournalScopeId, StringComparison.Ordinal)
                                && string.Equals(reader.IsDBNull(11) ? null : reader.GetString(11), request.JournalPathDigest, StringComparison.Ordinal)
                                && reader.GetInt32(12) == request.PayloadVersion;
                            bool recoveryOnly = reader.GetInt64(7) != 0;
                            if (!same)
                            {
                                error = "request_id is already owned by a different context or immutable payload";
                                transaction.Rollback();
                                return false;
                            }
                            if (recoveryOnly)
                            {
                                error = "restored request ownership is query-only; resubmission and cancellation are disabled";
                                transaction.Rollback();
                                return false;
                            }
                            transaction.Commit();
                            return true;
                        }
                    }
                }

                using (SqliteCommand insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = @"
INSERT INTO request_owners(request_id,context_id,tool_name,canonical_parameters_json,payload_digest,
 complete_target_json,owner_id,provider_generation,state,recovery_query_only,created_at_utc,updated_at_utc,
 is_write,risk_keys_json,journal_scope_id,journal_path_digest,payload_version)
VALUES($id,$context,$tool,$parameters,$digest,$target,$owner,$generation,'reserved',0,$created,$updated,
 $is_write,$risk_keys,$scope,$path_digest,$payload_version);";
                    insert.Parameters.AddWithValue("$id", request.RequestId);
                    insert.Parameters.AddWithValue("$context", request.ContextId);
                    insert.Parameters.AddWithValue("$tool", request.ToolName);
                    insert.Parameters.AddWithValue("$parameters", request.CanonicalParametersJson);
                    insert.Parameters.AddWithValue("$digest", request.PayloadDigest);
                    insert.Parameters.AddWithValue("$target", request.CompleteTargetJson);
                    insert.Parameters.AddWithValue("$owner", request.OwnerId);
                    insert.Parameters.AddWithValue("$generation", request.ProviderGeneration);
                    insert.Parameters.AddWithValue("$created", FormatTimestamp(request.CreatedAtUtc));
                    insert.Parameters.AddWithValue("$updated", FormatTimestamp(request.UpdatedAtUtc));
                    insert.Parameters.AddWithValue("$is_write", request.IsWrite ? 1 : 0);
                    insert.Parameters.AddWithValue("$risk_keys", JsonConvert.SerializeObject(request.RiskKeys));
                    insert.Parameters.AddWithValue("$scope", (object)request.JournalScopeId ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$path_digest", (object)request.JournalPathDigest ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$payload_version", request.PayloadVersion);
                    insert.ExecuteNonQuery();
                }
                transaction.Commit();
                return true;
            }
        }

        internal void UpdateState(string requestId, BoundPluginRequestState state)
        {
            ThrowIfDisposed();
            using (SqliteConnection connection = OpenConnection())
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE request_owners SET state=$state,updated_at_utc=$updated WHERE request_id=$id;";
                command.Parameters.AddWithValue("$state", StateText(state));
                command.Parameters.AddWithValue("$updated", FormatTimestamp(DateTimeOffset.UtcNow));
                command.Parameters.AddWithValue("$id", requestId);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("Durable request ownership disappeared during state update.");
            }
        }

        internal bool TryRecordSnapshot(string requestId, long sequence, string snapshotDigest, out string error)
        {
            ThrowIfDisposed();
            error = null;
            using (SqliteConnection connection = OpenConnection())
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                long? currentSequence = null;
                string currentDigest = null;
                using (SqliteCommand read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText = "SELECT plugin_sequence,snapshot_digest FROM request_owners WHERE request_id=$id;";
                    read.Parameters.AddWithValue("$id", requestId);
                    using (SqliteDataReader reader = read.ExecuteReader())
                    {
                        if (!reader.Read())
                        {
                            error = "request ownership is unavailable";
                            transaction.Rollback();
                            return false;
                        }
                        if (!reader.IsDBNull(0)) currentSequence = reader.GetInt64(0);
                        if (!reader.IsDBNull(1)) currentDigest = reader.GetString(1);
                    }
                }

                if (currentSequence.HasValue && sequence < currentSequence.Value)
                {
                    error = "plugin snapshot sequence moved backwards";
                    transaction.Rollback();
                    return false;
                }
                if (currentSequence.HasValue && sequence == currentSequence.Value)
                {
                    if (!string.Equals(currentDigest, snapshotDigest, StringComparison.Ordinal))
                    {
                        error = "plugin returned conflicting snapshots for the same sequence";
                        transaction.Rollback();
                        return false;
                    }
                    transaction.Commit();
                    return true;
                }

                using (SqliteCommand update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE request_owners SET plugin_sequence=$sequence,snapshot_digest=$digest,updated_at_utc=$updated WHERE request_id=$id;";
                    update.Parameters.AddWithValue("$sequence", sequence);
                    update.Parameters.AddWithValue("$digest", snapshotDigest);
                    update.Parameters.AddWithValue("$updated", FormatTimestamp(DateTimeOffset.UtcNow));
                    update.Parameters.AddWithValue("$id", requestId);
                    update.ExecuteNonQuery();
                }
                transaction.Commit();
                return true;
            }
        }

        private void InitializeAndRecover()
        {
            using (SqliteConnection connection = OpenConnection())
            {
                string check;
                using (SqliteCommand quickCheck = connection.CreateCommand())
                {
                    quickCheck.CommandText = "PRAGMA quick_check;";
                    check = Convert.ToString(quickCheck.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
                if (!string.IsNullOrEmpty(check) && !string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Bound-plugin ownership database failed SQLite integrity check.");

                long version;
                using (SqliteCommand versionQuery = connection.CreateCommand())
                {
                    versionQuery.CommandText = "PRAGMA user_version;";
                    version = (long)versionQuery.ExecuteScalar();
                }
                if (version > SchemaVersion)
                    throw new InvalidDataException("Bound-plugin ownership database schema is newer than this server supports.");

                using (SqliteTransaction transaction = connection.BeginTransaction())
                using (SqliteCommand migrate = connection.CreateCommand())
                {
                    migrate.Transaction = transaction;
                    migrate.CommandText = @"
CREATE TABLE IF NOT EXISTS bound_contexts(
 context_id TEXT PRIMARY KEY COLLATE BINARY,
 complete_target_json TEXT NOT NULL,
 endpoint_json TEXT NOT NULL,
 created_at_utc TEXT NOT NULL,
 journal_scope_id TEXT NULL,
 journal_path_digest TEXT NULL
);
CREATE TABLE IF NOT EXISTS request_owners(
 request_id TEXT PRIMARY KEY COLLATE BINARY,
 context_id TEXT NOT NULL REFERENCES bound_contexts(context_id) ON DELETE RESTRICT,
 tool_name TEXT NOT NULL,
 canonical_parameters_json TEXT NOT NULL,
 payload_digest TEXT NOT NULL,
 complete_target_json TEXT NOT NULL,
 owner_id TEXT NOT NULL,
 provider_generation TEXT NOT NULL,
 state TEXT NOT NULL CHECK(state IN ('reserved','unknown','confirmed')),
 recovery_query_only INTEGER NOT NULL CHECK(recovery_query_only IN (0,1)),
 created_at_utc TEXT NOT NULL,
 updated_at_utc TEXT NOT NULL,
 plugin_sequence INTEGER NULL,
 snapshot_digest TEXT NULL,
 is_write INTEGER NOT NULL DEFAULT 0 CHECK(is_write IN (0,1)),
 risk_keys_json TEXT NOT NULL DEFAULT '[]',
 journal_scope_id TEXT NULL,
 journal_path_digest TEXT NULL,
 payload_version INTEGER NOT NULL DEFAULT 1 CHECK(payload_version IN (1,2))
);
CREATE INDEX IF NOT EXISTS ix_request_owners_context ON request_owners(context_id);";
                    migrate.ExecuteNonQuery();

                    EnsureColumn(connection, transaction, "bound_contexts", "journal_scope_id", "TEXT NULL");
                    EnsureColumn(connection, transaction, "bound_contexts", "journal_path_digest", "TEXT NULL");
                    EnsureColumn(connection, transaction, "request_owners", "is_write", "INTEGER NOT NULL DEFAULT 0 CHECK(is_write IN (0,1))");
                    EnsureColumn(connection, transaction, "request_owners", "risk_keys_json", "TEXT NOT NULL DEFAULT '[]'");
                    EnsureColumn(connection, transaction, "request_owners", "journal_scope_id", "TEXT NULL");
                    EnsureColumn(connection, transaction, "request_owners", "journal_path_digest", "TEXT NULL");
                    EnsureColumn(connection, transaction, "request_owners", "payload_version", "INTEGER NOT NULL DEFAULT 1 CHECK(payload_version IN (1,2))");

                    using (SqliteCommand setVersion = connection.CreateCommand())
                    {
                        setVersion.Transaction = transaction;
                        setVersion.CommandText = "PRAGMA user_version=2;";
                        setVersion.ExecuteNonQuery();
                    }
                    transaction.Commit();
                }

                // A newly opened server may only inspect old requests. In-flight
                // means the previous server may have died at any point before or
                // after plugin admission, so it becomes OutcomeUnknown.
                using (SqliteTransaction transaction = connection.BeginTransaction())
                using (SqliteCommand recover = connection.CreateCommand())
                {
                    recover.Transaction = transaction;
                    recover.CommandText = @"
UPDATE request_owners SET state='unknown',updated_at_utc=$now WHERE state='reserved';
UPDATE request_owners SET recovery_query_only=1;";
                    recover.Parameters.AddWithValue("$now", FormatTimestamp(DateTimeOffset.UtcNow));
                    recover.ExecuteNonQuery();
                    transaction.Commit();
                }
            }
        }

        private static void EnsureColumn(SqliteConnection connection, SqliteTransaction transaction,
            string table, string name, string declaration)
        {
            using (SqliteCommand info = connection.CreateCommand())
            {
                info.Transaction = transaction;
                info.CommandText = "PRAGMA table_info(" + table + ");";
                using (SqliteDataReader reader = info.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (string.Equals(reader.GetString(1), name, StringComparison.OrdinalIgnoreCase))
                            return;
                    }
                }
            }

            using (SqliteCommand alter = connection.CreateCommand())
            {
                alter.Transaction = transaction;
                alter.CommandText = "ALTER TABLE " + table + " ADD COLUMN " + name + " " + declaration + ";";
                alter.ExecuteNonQuery();
            }
        }

        private SqliteConnection OpenConnection()
        {
            ThrowIfDisposed();
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            try
            {
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=3000; PRAGMA synchronous=FULL;";
                    command.ExecuteNonQuery();
                }
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        private static string SerializeEndpoint(DiscoveryInfo discovery) => JsonConvert.SerializeObject(new
        {
            schema_version = discovery.SchemaVersion,
            acad_year = discovery.AcadYear,
            target = discovery.Target,
            transport = discovery.Transport,
            host = discovery.Host,
            port = discovery.Port,
            pipe_name = discovery.PipeName,
            control_pipe_name = discovery.ControlPipeName,
            token = discovery.Token,
            pid = discovery.Pid,
            started_at_utc = discovery.StartedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        });

        private static JObject ParseObjectPreservingStrings(string json)
        {
            using var input = new StringReader(json);
            using var reader = new JsonTextReader(input) { DateParseHandling = DateParseHandling.None };
            JObject result = JObject.Load(reader);
            if (reader.Read())
                throw new JsonReaderException("Trailing JSON data is not allowed.");
            return result;
        }

        private static DiscoveryInfo DeserializeEndpoint(string json)
        {
            JObject value = ParseObjectPreservingStrings(json);
            return new DiscoveryInfo
            {
                SchemaVersion = Int(value, "schema_version"),
                AcadYear = OptionalInt(value, "acad_year", 0),
                Target = RequiredString(value, "target"),
                Transport = RequiredString(value, "transport"),
                Host = OptionalString(value, "host"),
                Port = value["port"]?.Type == JTokenType.Null ? null : (int?)Int(value, "port"),
                PipeName = OptionalString(value, "pipe_name"),
                ControlPipeName = OptionalString(value, "control_pipe_name"),
                Token = RequiredString(value, "token"),
                Pid = Int(value, "pid"),
                StartedAtUtc = ParseDiscoveryTimestamp(OptionalString(value, "started_at_utc"))
            };
        }

        private static string ComputePayloadDigest(BoundPluginRequestOwnership request)
        {
            if (request.PayloadVersion == 1)
            {
                string legacyMaterial = JsonConvert.SerializeObject(new
                {
                    digest_version = 1,
                    request_id = request.RequestId,
                    context_id = request.ContextId,
                    tool_name = request.ToolName,
                    canonical_parameters_json = request.CanonicalParametersJson,
                    complete_target_json = request.CompleteTargetJson,
                    owner_id = request.OwnerId,
                    provider_generation = request.ProviderGeneration
                });
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacyMaterial)));
            }

            if (request.PayloadVersion != 2)
                throw new InvalidDataException("Bound-plugin ownership payload version is unsupported.");

            string material = JsonConvert.SerializeObject(new
            {
                digest_version = 2,
                request_id = request.RequestId,
                context_id = request.ContextId,
                tool_name = request.ToolName,
                canonical_parameters_json = request.CanonicalParametersJson,
                complete_target_json = request.CompleteTargetJson,
                owner_id = request.OwnerId,
                provider_generation = request.ProviderGeneration,
                is_write = request.IsWrite,
                risk_keys = request.RiskKeys,
                journal_scope_id = request.JournalScopeId,
                journal_path_digest = request.JournalPathDigest
            });
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        }

        private static string[] DeserializeRiskKeys(string json)
        {
            try
            {
                JArray values = JArray.Parse(json);
                if (values.Any(value => value.Type != JTokenType.String))
                    throw new InvalidDataException("Stored risk-key set contains a non-string value.");

                string[] keys = values.Select(value => (string)value).ToArray();
                string[] normalized = keys.Select(RequestLedger.NormalizeRiskKey)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToArray();
                if (!keys.SequenceEqual(normalized, StringComparer.Ordinal))
                    throw new InvalidDataException("Stored risk-key set is not canonical.");
                return keys;
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch
            {
                throw new InvalidDataException("Stored risk-key set is invalid.");
            }
        }

        private static void ValidateOwnershipShape(BoundPluginRequestOwnership request)
        {
            if (request.PayloadVersion == 1)
            {
                if (request.IsWrite || request.RiskKeys.Count != 0
                    || request.JournalScopeId != null || request.JournalPathDigest != null)
                    throw new InvalidDataException("Legacy bound request ownership must be read-only and unscoped.");
                return;
            }

            bool scopeMissing = request.JournalScopeId == null && request.JournalPathDigest == null;
            bool scopeValid = !scopeMissing
                && Guid.TryParseExact(request.JournalScopeId, "N", out Guid scopeGuid)
                && scopeGuid != Guid.Empty
                && string.Equals(request.JournalScopeId, scopeGuid.ToString("N"), StringComparison.Ordinal)
                && request.JournalPathDigest != null
                && request.JournalPathDigest.Length == 64
                && request.JournalPathDigest.All(character => (character >= '0' && character <= '9')
                    || (character >= 'A' && character <= 'F'));
            if (!scopeMissing && !scopeValid)
                throw new InvalidDataException("Stored request ownership journal scope or path digest is invalid.");

            if (!request.IsWrite)
            {
                if (request.RiskKeys.Count != 0
                    || (request.ToolName == BoundPluginContextStore.D1WriteToolName || request.ToolName == BoundPluginContextStore.H1WriteToolName || request.ToolName == BoundPluginContextStore.FoundationWriteToolName))
                    throw new InvalidDataException("Read-only request ownership contains write identity.");
                return;
            }

            if (request.PayloadVersion != 2
                || !scopeValid
                || !(request.ToolName == BoundPluginContextStore.D1WriteToolName || request.ToolName == BoundPluginContextStore.H1WriteToolName || request.ToolName == BoundPluginContextStore.FoundationWriteToolName)
                || request.RiskKeys.Count != 1
                || !request.RiskKeys[0].StartsWith("PATH:", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Stored D1 write ownership has invalid scope or risk identity.");
            }

            try
            {
                if (!string.Equals(RequestLedger.NormalizeRiskKey(request.RiskKeys[0]),
                    request.RiskKeys[0], StringComparison.Ordinal))
                    throw new InvalidDataException("Stored D1 write risk key is not normalized.");
            }
            catch (ArgumentException)
            {
                throw new InvalidDataException("Stored D1 write risk key is invalid.");
            }
        }

        internal static string CreatePayloadDigest(BoundPluginRequestOwnership request) => ComputePayloadDigest(request);

        private static BoundPluginRequestState ParseState(string value) => value switch
        {
            "reserved" => BoundPluginRequestState.Reserved,
            "unknown" => BoundPluginRequestState.OutcomeUnknown,
            "confirmed" => BoundPluginRequestState.Confirmed,
            _ => throw new InvalidDataException("Bound-plugin ownership database contains an invalid request state.")
        };

        private static string StateText(BoundPluginRequestState state) => state switch
        {
            BoundPluginRequestState.Reserved => "reserved",
            BoundPluginRequestState.OutcomeUnknown => "unknown",
            BoundPluginRequestState.Confirmed => "confirmed",
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };

        private static DateTimeOffset ParseTimestamp(string value)
        {
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
                throw new InvalidDataException("Bound-plugin ownership database contains an invalid timestamp.");
            return result.ToUniversalTime();
        }

        private static DateTime? ParseDiscoveryTimestamp(string value)
        {
            if (value == null) return null;
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                throw new InvalidDataException("Stored discovery timestamp is invalid.");
            return parsed.UtcDateTime;
        }

        private static string FormatTimestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        private static string RequiredString(JObject value, string name) =>
            value[name]?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)value[name])
                ? (string)value[name]
                : throw new InvalidDataException("Stored endpoint is missing " + name + ".");

        private static string OptionalString(JObject value, string name) =>
            value[name] == null || value[name].Type == JTokenType.Null ? null
            : value[name].Type == JTokenType.String ? (string)value[name]
            : throw new InvalidDataException("Stored endpoint field " + name + " is invalid.");

        private static int Int(JObject value, string name) =>
            value[name]?.Type == JTokenType.Integer ? (int)value[name]
            : throw new InvalidDataException("Stored endpoint field " + name + " is invalid.");

        private static int OptionalInt(JObject value, string name, int fallback) =>
            value[name] == null || value[name].Type == JTokenType.Null ? fallback
            : value[name].Type == JTokenType.Integer ? (int)value[name]
            : throw new InvalidDataException("Stored endpoint field " + name + " is invalid.");

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SqliteBoundPluginOwnershipStore));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ownerLock.Dispose();
        }
    }
}
