using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Adapters.Oracle;
using MigrationStudio.Core.Jobs;
using MigrationStudio.Core.Mapping;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Sql;
using MigrationStudio.Logic;
using MigrationStudio.Ui;

namespace MigrationStudio.Services
{
    internal sealed class MappingOperations
    {
        private readonly StudioState _state;
        private readonly ConnectionService _connections;
        private readonly Window _owner;

        public MappingOperations(StudioState state, ConnectionService connections, Window owner)
        {
            _state = state;
            _connections = connections;
            _owner = owner;
        }

        public void SetOwner(Window owner)
        {
        }

        public bool HasMetadata()
        {
            return _state.SourceMeta() != null && _state.TargetMeta() != null;
        }

        public Mapping AddTableMapping(string source, string target, string mode)
        {
            var m = MappingFactory.CreateTableMapping(_state.SourceMeta(), _state.TargetMeta(), source, target, mode);
            _state.Job.Mappings.Add(m);
            _state.MarkChanged();
            return m;
        }

        public Mapping AddSqlMapping(string name, string target, string mode, string startTable)
        {
            var m = MappingFactory.CreateSqlMapping(name, target, mode, startTable, _state.SourceMeta(), _state.TargetMeta());
            _state.Job.Mappings.Add(m);
            _state.MarkChanged();
            return m;
        }

        public int ApplyAutoMatch(IList<TableMatch> matches)
        {
            var n = 0;
            foreach (var match in matches ?? Array.Empty<TableMatch>())
            {
                var exists = _state.Job.Mappings.Any(x =>
                    string.Equals(x.Source, match.Source, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Target, match.Target, StringComparison.OrdinalIgnoreCase));
                if (exists)
                {
                    continue;
                }

                _state.Job.Mappings.Add(MappingFactory.CreateTableMapping(
                    _state.SourceMeta(), _state.TargetMeta(), match.Source, match.Target, WriteModes.Merge));
                n++;
            }

            if (n > 0)
            {
                _state.MarkChanged();
            }

            return n;
        }

        public void DeleteMapping(string id)
        {
            var m = _state.Mapping(id);
            if (m == null)
            {
                return;
            }

            _state.Job.Mappings.Remove(m);
            _state.RemoveMappingSession(id);
            if (string.Equals(_state.Ui.SelMapping, id, StringComparison.Ordinal))
            {
                _state.Ui.SelMapping = null;
            }

            _state.MarkChanged();
        }

        public void ExportTemplate(string name)
        {
            if (_state.IsRunning)
            {
                return;
            }

            if (_state.Job.Mappings == null || _state.Job.Mappings.Count == 0)
            {
                Kit.Toast(_owner, "내보낼 매핑이 없습니다", "warn");
                return;
            }

            var dlg = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = (name ?? "mapping") + ".json" };
            if (dlg.ShowDialog(_owner) != true)
            {
                return;
            }

            try
            {
                var json = MappingTemplate.Export(_state.Job, name);
                File.WriteAllText(dlg.FileName, json, new UTF8Encoding(false));
                Kit.Toast(_owner, "템플릿을 내보냈습니다", "ok");
            }
            catch (Exception ex)
            {
                Dialogs.Show(_owner, "내보내기", ex.Message, true);
            }
        }

        public void ImportTemplate()
        {
            if (_state.IsRunning)
            {
                return;
            }

            var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
            if (dlg.ShowDialog(_owner) != true)
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(dlg.FileName, Encoding.UTF8);
                var result = MappingTemplate.Apply(_state.Job, json);
                _state.MarkChanged();
                Kit.Toast(_owner, "템플릿 적용: " + result.Added + "개 추가 · " + result.Replaced + "개 바꿈", "ok");
            }
            catch (JobFileException ex)
            {
                Dialogs.Show(_owner, "가져오기", ex.Message, true);
            }
        }

        public IDatabaseAdapter ResolveAdapter()
        {
            if (AppServices.DatabaseAdapter != null)
            {
                return AppServices.DatabaseAdapter;
            }

            var profile = _state.ProfileForRole(Roles.Source);
            return DatabaseAdapters.For(profile != null ? profile.Kind : "oracle");
        }

        public async Task<SqlValidationResult> ValidateSqlMappingAsync(Mapping mapping, CancellationToken ct)
        {
            var profile = _state.ProfileForRole(Roles.Source);
            if (profile == null)
            {
                return null;
            }

            var session = _state.Conn[Roles.Source];
            if (session.Status != ConnStatus.Ok)
            {
                await _connections.TestAsync(Roles.Source).ConfigureAwait(true);
                if (session.Status != ConnStatus.Ok)
                {
                    return null;
                }
            }

            var password = _connections.PasswordFor(profile, "SQL 검증에 필요합니다.");
            if (password == null)
            {
                return null;
            }

            var target = ConnectionTarget.From(profile, password);
            var adapter = ResolveAdapter();
            var svc = new SqlSourceService(adapter);
            var schema = _state.Job.Source != null ? _state.Job.Source.Schema : profile.DefaultSchema;
            return await svc.ValidateAsync(mapping, target, schema, _state.SourceMeta(), _state.TargetMeta(), ct)
                .ConfigureAwait(true);
        }

        public async Task<QueryResult> PreviewSqlMappingAsync(Mapping mapping, int rows, CancellationToken ct)
        {
            var profile = _state.ProfileForRole(Roles.Source);
            if (profile == null)
            {
                return null;
            }

            var session = _state.Conn[Roles.Source];
            if (session.Status != ConnStatus.Ok)
            {
                await _connections.TestAsync(Roles.Source).ConfigureAwait(true);
                if (session.Status != ConnStatus.Ok)
                {
                    return null;
                }
            }

            var password = _connections.PasswordFor(profile, "SQL 미리보기에 필요합니다.");
            if (password == null)
            {
                return null;
            }

            var target = ConnectionTarget.From(profile, password);
            var schema = _state.Job.Source != null ? _state.Job.Source.Schema : profile.DefaultSchema;
            return await new SqlSourceService(ResolveAdapter()).PreviewAsync(mapping, target, schema, rows, ct).ConfigureAwait(true);
        }
    }
}
