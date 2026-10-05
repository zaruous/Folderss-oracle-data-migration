using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Adapters.Oracle;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using MigrationStudio.Core.Validation;
using MigrationStudio.Logic;

namespace MigrationStudio.Services
{
    internal sealed class ValidationService : IValidationService
    {
        private readonly StudioState _state;
        private readonly ConnectionService _connections;

        public ValidationService(StudioState state, ConnectionService connections)
        {
            _state = state;
            _connections = connections;
        }

        public async Task<List<ValidationItem>> RunPreAsync(Action<ValidationItem> onItem, CancellationToken ct)
        {
            var ctx = BuildContext();
            var adapter = ResolveAdapter();
            var engine = new ValidationEngine(adapter);
            return await engine.RunPreAsync(ctx, onItem, ct).ConfigureAwait(false);
        }

        private ValidationContext BuildContext()
        {
            var ctx = new ValidationContext
            {
                Job = _state.Job,
                Settings = _state.Settings,
                SqlDescribe = BuildSqlDescribe()
            };

            FillRole(ctx, Roles.Source, true);
            FillRole(ctx, Roles.Target, false);
            return ctx;
        }

        private void FillRole(ValidationContext ctx, string role, bool source)
        {
            var profile = _state.ProfileForRole(role);
            if (profile == null)
            {
                return;
            }

            var password = _connections.PasswordFor(profile, "검증에 접속 비밀번호가 필요합니다.");
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
                ctx.SourceProfile = profile;
                ctx.Source = target;
                ctx.SourceMeta = session != null ? session.Metadata : null;
                ctx.SourceTest = session != null && session.Status == ConnStatus.Ok ? session.Result : null;
            }
            else
            {
                ctx.TargetProfile = profile;
                ctx.Target = target;
                ctx.TargetMeta = session != null ? session.Metadata : null;
                ctx.TargetTest = session != null && session.Status == ConnStatus.Ok ? session.Result : null;
            }
        }

        private Dictionary<string, IList<MigrationStudio.Core.Adapters.QueryColumn>> BuildSqlDescribe()
        {
            var map = new Dictionary<string, IList<MigrationStudio.Core.Adapters.QueryColumn>>(StringComparer.Ordinal);
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
