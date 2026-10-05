using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Adapters.Oracle;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using MigrationStudio.Core.Sql;
using MigrationStudio.Core.Validation;
using MigrationStudio.Logic;
using MappingModel = MigrationStudio.Core.Model.Mapping;

namespace MigrationStudio.Services
{
    /// <summary>실행 후 검증 — 접속·메타·작업 계획으로 <see cref="PostValidationEngine"/>을 호출한다.</summary>
    internal sealed class PostValidationService : IPostValidationRunner
    {
        private readonly StudioState _state;
        private readonly ConnectionService _connections;

        public PostValidationService(StudioState state, ConnectionService connections)
        {
            _state = state;
            _connections = connections;
        }

        public async Task<List<PostItem>> RunPostAsync(PostValidationRequest request, Action<PostItem> onItem, CancellationToken ct)
        {
            var ctx = BuildContext(request);
            if (ctx.Source == null || ctx.Target == null || ctx.SourceMeta == null || ctx.TargetMeta == null)
            {
                var warn = new PostItem
                {
                    Group = "—",
                    Check = "실행 후 검증",
                    Level = CheckLevels.Warn,
                    Detail = "원본·대상 접속과 메타데이터를 먼저 준비하세요."
                };
                if (onItem != null)
                {
                    onItem(warn);
                }

                return new List<PostItem> { warn };
            }

            var adapter = ResolveAdapter();
            var engine = new PostValidationEngine(adapter, ctx);
            return await engine.RunAsync(request, onItem, ct).ConfigureAwait(false);
        }

        private PostValidationContext BuildContext(PostValidationRequest request)
        {
            var ctx = new PostValidationContext { Job = _state.Job };
            FillRole(ctx, Roles.Source, true);
            FillRole(ctx, Roles.Target, false);
            ctx.SqlDescribe = BuildSqlDescribe();

            if (request != null && request.Final != null && request.Final.Tasks != null)
            {
                ctx.Plan = BuildPlanItems(request.Final.Tasks);
            }

            if (_state.Run != null && request != null && string.Equals(_state.Run.RunId, request.RunId, StringComparison.Ordinal))
            {
                foreach (var pair in _state.Run.BaseRows)
                {
                    ctx.BaseRows[pair.Key] = pair.Value;
                }
            }

            return ctx;
        }

        private List<PlanItem> BuildPlanItems(IList<TaskSnapshot> tasks)
        {
            var list = new List<PlanItem>();
            var job = _state.Job;
            if (job == null || job.Mappings == null)
            {
                return list;
            }

            var usedErrorTables = new HashSet<string>(StringComparer.Ordinal);
            foreach (var task in tasks)
            {
                var mapping = job.Mappings.FirstOrDefault(m => string.Equals(m.Id, task.Key, StringComparison.Ordinal));
                if (mapping == null || !mapping.Use)
                {
                    continue;
                }

                ConnectionRoleState srcSession;
                ConnectionRoleState tgtSession;
                _state.Conn.TryGetValue(Roles.Source, out srcSession);
                _state.Conn.TryGetValue(Roles.Target, out tgtSession);
                var srcMeta = srcSession != null ? srcSession.Metadata : null;
                var tgtMeta = tgtSession != null ? tgtSession.Metadata : null;
                var source = SourceOf(mapping, srcMeta);
                var target = tgtMeta != null ? tgtMeta.FindTable(mapping.Target) : null;
                var item = new PlanItem
                {
                    Key = mapping.Id,
                    Mapping = mapping,
                    Label = !string.IsNullOrEmpty(task.Label) ? task.Label : mapping.Label,
                    ScopeTotal = task.Total,
                    SourceMetadata = source,
                    TargetMetadata = target,
                    WriteColumns = SqlGenerator.WriteColumns(mapping, target),
                    ErrorTable = ErrorTableNamer.Resolve(job.Strategy, mapping.Target, usedErrorTables)
                };
                list.Add(item);
            }

            return list;
        }

        private static TableMetadata SourceOf(MappingModel m, SchemaMetadata srcMeta)
        {
            if (m == null)
            {
                return null;
            }

            if (!m.IsSql)
            {
                return srcMeta != null ? srcMeta.FindTable(m.Source) : null;
            }

            return SqlSourceService.VirtualTable(m, new List<QueryColumn>(), srcMeta);
        }

        private void FillRole(PostValidationContext ctx, string role, bool source)
        {
            var profile = _state.ProfileForRole(role);
            if (profile == null)
            {
                return;
            }

            var password = _connections.PasswordFor(profile, "실행 후 검증에 접속 비밀번호가 필요합니다.");
            if (password == null)
            {
                return;
            }

            var target = ConnectionTarget.From(profile, password);
            ConnectionRoleState session;
            if (!_state.Conn.TryGetValue(role, out session))
            {
                session = null;
            }

            if (source)
            {
                ctx.Source = target;
                ctx.SourceMeta = session != null ? session.Metadata : null;
            }
            else
            {
                ctx.Target = target;
                ctx.TargetMeta = session != null ? session.Metadata : null;
            }
        }

        private Dictionary<string, IList<QueryColumn>> BuildSqlDescribe()
        {
            var map = new Dictionary<string, IList<QueryColumn>>(StringComparer.Ordinal);
            foreach (var pair in _state.SqlDescribe)
            {
                if (pair.Value != null && pair.Value.Columns != null)
                {
                    map[pair.Key] = pair.Value.Columns;
                }
            }

            return map;
        }

        private static IDatabaseAdapter ResolveAdapter()
        {
            if (AppServices.DatabaseAdapter != null)
            {
                return AppServices.DatabaseAdapter;
            }

            return DatabaseAdapters.For("oracle");
        }
    }
}
