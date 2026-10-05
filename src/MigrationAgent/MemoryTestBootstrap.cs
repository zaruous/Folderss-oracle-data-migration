using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Sql;

namespace MigrationAgent
{
    internal static class MemoryTestBootstrap
    {
        internal sealed class MemoryTable
        {
            internal MemoryTable(string name, List<ColumnMetadata> columns, List<string> keys)
            {
                Name = name;
                Columns = columns;
                Keys = keys;
                Rows = new List<object[]>();
            }

            internal string Name { get; private set; }
            internal List<ColumnMetadata> Columns { get; private set; }
            internal List<string> Keys { get; private set; }
            internal List<object[]> Rows { get; private set; }

            internal int Ordinal(string column)
            {
                return Columns.FindIndex(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
            }
        }

        public static void Create(RunSpec spec, out ISourceFactory source, out ITargetFactory target, out ICheckpointStore store)
        {
            var path = Environment.GetEnvironmentVariable("MIGRATION_AGENT_TEST_MEMORY_JSON");
            var tables = LoadTables(path, spec);
            store = new LocalCheckpointStore(Path.Combine(spec.DataDirectory ?? Path.GetTempPath(), "checkpoints"));
            source = new MemSource(tables);
            target = new MemTarget(tables, store);
        }

        private static Dictionary<string, MemoryTable> LoadTables(string path, RunSpec spec)
        {
            var map = new Dictionary<string, MemoryTable>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var json = File.ReadAllText(path);
                using (var doc = JsonDocument.Parse(json))
                {
                    foreach (var tableProp in doc.RootElement.EnumerateObject())
                    {
                        var table = new MemoryTable(tableProp.Name, new List<ColumnMetadata>(), new List<string> { "ID" });
                        table.Columns.Add(new ColumnMetadata { Name = "ID", Type = "NUMBER(18)", Nullable = false, PrimaryKey = true });
                        table.Columns.Add(new ColumnMetadata { Name = "NAME", Type = "VARCHAR2(20)", Nullable = false });
                        foreach (var rowEl in tableProp.Value.EnumerateArray())
                        {
                            table.Rows.Add(new object[] { rowEl[0].GetDecimal(), rowEl[1].GetString() });
                        }

                        map[table.Name] = table;
                    }
                }
            }

            if (spec != null && spec.Plan != null)
            {
                foreach (var item in spec.Plan)
                {
                    if (item.Mapping == null)
                    {
                        continue;
                    }

                    if (!map.ContainsKey(item.Mapping.Source))
                    {
                        var src = new MemoryTable(item.Mapping.Source,
                            new List<ColumnMetadata>
                            {
                                new ColumnMetadata { Name = "ID", Type = "NUMBER(18)", Nullable = false, PrimaryKey = true },
                                new ColumnMetadata { Name = "NAME", Type = "VARCHAR2(20)", Nullable = false }
                            },
                            new List<string> { "ID" });
                        for (var i = 1; i <= Math.Max(1, item.ScopeTotal); i++)
                        {
                            src.Rows.Add(new object[] { (decimal)i, "N-" + i.ToString(CultureInfo.InvariantCulture) });
                        }

                        map[src.Name] = src;
                    }

                    if (!map.ContainsKey(item.Mapping.Target))
                    {
                        map[item.Mapping.Target] = new MemoryTable(item.Mapping.Target,
                            new List<ColumnMetadata>
                            {
                                new ColumnMetadata { Name = "ID", Type = "NUMBER(18)", Nullable = false, PrimaryKey = true },
                                new ColumnMetadata { Name = "NAME", Type = "VARCHAR2(20)", Nullable = false }
                            },
                            new List<string> { "ID" });
                    }
                }
            }

            return map;
        }

        private sealed class MemSource : ISourceFactory
        {
            private readonly Dictionary<string, MemoryTable> _tables;

            internal MemSource(Dictionary<string, MemoryTable> tables)
            {
                _tables = tables;
            }

            public System.Threading.Tasks.Task<ISourceReader> OpenAsync(PlanItem item, KeyRange range, string lastValue, int fetchSize, System.Threading.CancellationToken cancellationToken)
            {
                var table = _tables[item.Mapping.Source];
                var cp = table.Ordinal(item.Mapping.CheckpointColumn);
                IEnumerable<object[]> rows = table.Rows;
                if (cp >= 0 && lastValue != null)
                {
                    rows = rows.Where(r => Compare(r[cp], lastValue) > 0);
                }

                var list = rows.Select(r => Project(table, item, r, cp)).ToList();
                var columns = item.WriteColumns.Select(c => c.Name).ToList();
                if (cp >= 0) columns.Add("MIG_CP_HIDDEN");
                return System.Threading.Tasks.Task.FromResult<ISourceReader>(new Reader(list, columns, cp >= 0 ? columns.Count - 1 : -1));
            }

            private static object[] Project(MemoryTable table, PlanItem item, object[] row, int cp)
            {
                var result = new object[item.WriteColumns.Count + (cp < 0 ? 0 : 1)];
                for (var i = 0; i < item.WriteColumns.Count; i++)
                {
                    var mapping = item.Mapping.FindColumn(item.WriteColumns[i].Name);
                    var ord = mapping == null ? -1 : table.Ordinal(mapping.Source);
                    result[i] = ord < 0 ? DBNull.Value : row[ord];
                }

                if (cp >= 0) result[result.Length - 1] = row[cp];
                return result;
            }

            private static int Compare(object value, string bound)
            {
                if (decimal.TryParse(bound, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                {
                    return Convert.ToDecimal(value, CultureInfo.InvariantCulture).CompareTo(number);
                }

                return string.Compare(Convert.ToString(value, CultureInfo.InvariantCulture), bound, StringComparison.Ordinal);
            }

            private sealed class Reader : ISourceReader
            {
                private readonly List<object[]> _rows;
                private int _pos;

                internal Reader(List<object[]> rows, IReadOnlyList<string> columns, int checkpointOrdinal)
                {
                    _rows = rows;
                    Columns = columns;
                    CheckpointOrdinal = checkpointOrdinal;
                }

                public IReadOnlyList<string> Columns { get; private set; }
                public int CheckpointOrdinal { get; private set; }

                public System.Threading.Tasks.Task<List<object[]>> ReadAsync(int maxRows, System.Threading.CancellationToken cancellationToken)
                {
                    var take = Math.Min(maxRows, _rows.Count - _pos);
                    var batch = take <= 0 ? new List<object[]>() : _rows.GetRange(_pos, take);
                    _pos += take;
                    return System.Threading.Tasks.Task.FromResult(batch);
                }

                public void Dispose() { }
            }
        }

        private sealed class MemTarget : ITargetFactory
        {
            private readonly Dictionary<string, MemoryTable> _tables;
            private readonly ICheckpointStore _store;

            internal MemTarget(Dictionary<string, MemoryTable> tables, ICheckpointStore store)
            {
                _tables = tables;
                _store = store;
            }

            public System.Threading.Tasks.Task<ITargetSession> OpenAsync(System.Threading.CancellationToken cancellationToken)
            {
                return System.Threading.Tasks.Task.FromResult<ITargetSession>(new Session(_tables, _store));
            }

            private sealed class Session : ITargetSession
            {
                private readonly Dictionary<string, MemoryTable> _tables;
                private readonly ICheckpointStore _store;

                internal Session(Dictionary<string, MemoryTable> tables, ICheckpointStore store)
                {
                    _tables = tables;
                    _store = store;
                }

                public System.Threading.Tasks.Task<WriteResult> WriteBatchAsync(PlanItem item, IReadOnlyList<WriteColumn> columns, List<object[]> rows, System.Threading.CancellationToken cancellationToken)
                {
                    var table = _tables[item.Mapping.Target];
                    foreach (var row in rows)
                    {
                        table.Rows.Add(row);
                    }

                    return System.Threading.Tasks.Task.FromResult(new WriteResult { Written = rows.Count, Inserted = rows.Count });
                }

                public System.Threading.Tasks.Task SaveCheckpointAsync(CheckpointRecord record, System.Threading.CancellationToken cancellationToken)
                {
                    return _store.SaveLocalAsync(record, cancellationToken);
                }

                public System.Threading.Tasks.Task CommitAsync() { return System.Threading.Tasks.Task.CompletedTask; }
                public System.Threading.Tasks.Task RollbackAsync() { return System.Threading.Tasks.Task.CompletedTask; }
                public System.Threading.Tasks.Task TruncateAsync(PlanItem item, System.Threading.CancellationToken cancellationToken)
                {
                    _tables[item.Mapping.Target].Rows.Clear();
                    return System.Threading.Tasks.Task.CompletedTask;
                }

                public System.Threading.Tasks.Task<long> CountExistingKeysAsync(PlanItem item, List<object[]> rows, System.Threading.CancellationToken cancellationToken)
                {
                    return System.Threading.Tasks.Task.FromResult(0L);
                }

                public void Dispose() { }
            }
        }
    }
}
