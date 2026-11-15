// Modified for CAD Agent Foundation by capskov (2026); derived from Bimwright DWG MCP pinned at 4d0f75646631186612af926944d7e33affc4757d. See NOTICE.md.
using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.DatabaseServices;

namespace Bimwright.Dwg.Plugin.Cad
{
    internal sealed class CadEntityQueryOptions
    {
        internal CadEntityQueryOptions(
            string entityType,
            string layer,
            int? colorIndex,
            int? limit,
            bool includeGeometry,
            CadQueryBounds bounds = null,
            int offset = 0)
        {
            EntityType = Normalize(entityType);
            Layer = Normalize(layer);
            ColorIndex = colorIndex;
            Limit = limit;
            IncludeGeometry = includeGeometry;
            Bounds = bounds;
            Offset = offset;
        }

        internal string EntityType { get; }
        internal string Layer { get; }
        internal int? ColorIndex { get; }
        internal int? Limit { get; }
        internal bool IncludeGeometry { get; }
        internal CadQueryBounds Bounds { get; }
        internal int Offset { get; }

        private static string Normalize(string value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    internal sealed class CadQueryBounds
    {
        internal CadQueryBounds(double minX, double minY, double maxX, double maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        internal double MinX { get; }
        internal double MinY { get; }
        internal double MaxX { get; }
        internal double MaxY { get; }
    }

    internal sealed class CadEntityQueryResult
    {
        internal CadEntityQueryResult(IReadOnlyList<object> entities, IReadOnlyList<string> handles, bool hasMore)
        {
            Entities = entities;
            Handles = handles;
            HasMore = hasMore;
        }

        internal IReadOnlyList<object> Entities { get; }
        internal IReadOnlyList<string> Handles { get; }
        internal bool HasMore { get; }
    }

    internal static class CadQueryService
    {
        private const int DefaultLimit = 500;
        private const int MaxLimit = 5000;

        internal static CadEntityQueryResult QueryEntities(Database db, CadEntityQueryOptions options)
        {
            if (db == null)
            {
                throw new ArgumentNullException(nameof(db));
            }

            options = options ?? new CadEntityQueryOptions(null, null, null, null, includeGeometry: false);
            var limit = ClampLimit(options.Limit);
            var entities = new List<object>();
            var handles = new List<string>();
            bool hasMore = false;
            int matchingCount = 0;

            using (var tx = db.TransactionManager.StartTransaction())
            {
                var modelSpace = OpenModelSpace(db, tx);
                foreach (ObjectId objectId in modelSpace)
                {
                    var entity = tx.GetObject(objectId, OpenMode.ForRead) as Entity;
                    if (entity == null || !Matches(entity, options))
                    {
                        continue;
                    }

                    if (options.Bounds != null && !IntersectsBounds(entity, options.Bounds))
                    {
                        continue;
                    }

                    if (matchingCount < options.Offset)
                    {
                        matchingCount++;
                        continue;
                    }

                    if (entities.Count >= limit)
                    {
                        // One matching entity beyond the returned page proves
                        // truncation without describing or returning that row.
                        hasMore = true;
                        break;
                    }

                    handles.Add(entity.Handle.ToString());
                    entities.Add(CadEntityProperties.Describe(entity, tx, options.IncludeGeometry));
                    matchingCount++;
                }

                tx.Commit();
            }

            return new CadEntityQueryResult(entities, handles, hasMore);
        }

        internal static int CountEntities(Database db, CadEntityQueryOptions options)
        {
            if (db == null)
            {
                throw new ArgumentNullException(nameof(db));
            }

            options = options ?? new CadEntityQueryOptions(null, null, null, null, includeGeometry: false);
            var count = 0;

            using (var tx = db.TransactionManager.StartTransaction())
            {
                var modelSpace = OpenModelSpace(db, tx);
                foreach (ObjectId objectId in modelSpace)
                {
                    var entity = tx.GetObject(objectId, OpenMode.ForRead) as Entity;
                    if (entity != null && Matches(entity, options))
                    {
                        count++;
                    }
                }

                tx.Commit();
            }

            return count;
        }

        internal static IReadOnlyList<string> QueryHandles(Database db, CadEntityQueryOptions options)
        {
            if (db == null)
            {
                throw new ArgumentNullException(nameof(db));
            }

            options = options ?? new CadEntityQueryOptions(null, null, null, null, includeGeometry: false);
            var limit = ClampLimit(options.Limit);
            var handles = new List<string>();

            using (var tx = db.TransactionManager.StartTransaction())
            {
                var modelSpace = OpenModelSpace(db, tx);
                foreach (ObjectId objectId in modelSpace)
                {
                    if (handles.Count >= limit)
                    {
                        break;
                    }

                    var entity = tx.GetObject(objectId, OpenMode.ForRead) as Entity;
                    if (entity == null || !Matches(entity, options))
                    {
                        continue;
                    }

                    handles.Add(entity.Handle.ToString());
                }

                tx.Commit();
            }

            return handles;
        }

        private static int ClampLimit(int? limit)
        {
            if (!limit.HasValue)
            {
                return DefaultLimit;
            }

            if (limit.Value < 1)
            {
                return 1;
            }

            if (limit.Value > MaxLimit)
            {
                return MaxLimit;
            }

            return limit.Value;
        }

        private static BlockTableRecord OpenModelSpace(Database db, Transaction tx)
        {
            var blockTable = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
            return (BlockTableRecord)tx.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        }

        private static bool Matches(Entity entity, CadEntityQueryOptions options)
        {
            if (!string.IsNullOrEmpty(options.EntityType) &&
                !string.Equals(entity.GetType().Name, options.EntityType, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(options.Layer) &&
                !string.Equals(entity.Layer, options.Layer, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (options.ColorIndex.HasValue && (int)entity.ColorIndex != options.ColorIndex.Value)
            {
                return false;
            }

            return true;
        }

        private static bool IntersectsBounds(Entity entity, CadQueryBounds bounds)
        {
            Extents3d extents;
            try
            {
                extents = entity.GeometricExtents;
            }
            catch (Exception)
            {
                throw new InvalidOperationException(
                    "spatial filter could not read geometric extents for handle " + entity.Handle);
            }

            Point3d min = extents.MinPoint;
            Point3d max = extents.MaxPoint;
            if (!IsFinite(min.X) || !IsFinite(min.Y) || !IsFinite(max.X) || !IsFinite(max.Y))
            {
                throw new InvalidOperationException(
                    "spatial filter found non-finite geometric extents for handle " + entity.Handle);
            }

            // GeometricExtents is WCS-aligned. Closed-interval overlap means
            // entities touching a rectangle edge are retained as candidates.
            return min.X <= bounds.MaxX && max.X >= bounds.MinX
                && min.Y <= bounds.MaxY && max.Y >= bounds.MinY;
        }

        private static bool IsFinite(double value)
            => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
