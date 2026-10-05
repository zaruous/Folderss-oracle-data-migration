using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Adapters.Oracle;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Settings;
using MigrationStudio.Core.Storage;
using MigrationStudio.Logic;
using MigrationStudio.Ui;

namespace MigrationStudio.Services
{
    internal sealed class ConnectionService
    {
        private readonly StudioState _state;
        private Window _owner;
        private readonly Dictionary<string, string> _passwords = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, CancellationTokenSource> _tests = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        private readonly Dictionary<string, CancellationTokenSource> _meta = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);

        public ConnectionService(StudioState state, Window owner)
        {
            _state = state;
            _owner = owner;
        }

        public void SetOwner(Window owner)
        {
            _owner = owner;
        }

        public void CancelAll()
        {
            CancelMap(_tests);
            CancelMap(_meta);
        }

        private static void CancelMap(Dictionary<string, CancellationTokenSource> map)
        {
            foreach (var pair in map)
            {
                try
                {
                    pair.Value.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            map.Clear();
        }

        public string PasswordFor(ConnectionProfile profile, string reason)
        {
            if (profile == null)
            {
                return null;
            }

            // DevHost(시연·검수 도구)가 가짜 어댑터를 쓸 때는 입력 대화상자가 화면 캡처를 막으므로 정해 둔 값을 쓴다
            if (AppServices.DevHostPassword != null)
            {
                return AppServices.DevHostPassword;
            }

            string cached;
            if (_passwords.TryGetValue(profile.Id, out cached))
            {
                return cached;
            }

            if (!string.IsNullOrEmpty(profile.ProtectedPassword) && profile.SavePassword)
            {
                string plain;
                if (PasswordProtector.TryUnprotect(profile.ProtectedPassword, out plain))
                {
                    return plain;
                }
            }

            var promptReason = !string.IsNullOrEmpty(profile.ProtectedPassword) && profile.SavePassword
                ? "저장된 비밀번호를 풀 수 없습니다(다른 PC·다른 Windows 사용자). 다시 입력하세요."
                : reason;
            var entered = Dialogs.PasswordPrompt(_owner, profile, promptReason, true);
            if (entered == null)
            {
                return null;
            }

            _passwords[profile.Id] = entered;
            return entered;
        }

        public void RememberPassword(string profileId, string password, bool remember)
        {
            if (remember && !string.IsNullOrEmpty(password))
            {
                _passwords[profileId] = password;
            }
        }

        public async Task TestAsync(string role)
        {
            ConnectionRoleState session;
            if (!_state.Conn.TryGetValue(role, out session) || session.Status == ConnStatus.Testing)
            {
                return;
            }

            var profile = _state.ProfileForRole(role);
            if (profile == null)
            {
                session.Status = ConnStatus.Error;
                session.Result = new ConnectionTestResult { Ok = false, Error = "마이그레이션 설정에서 접속을 고르세요", TestedAt = DateTime.Now };
                _state.Notify(ChangeScope.Session);
                return;
            }

            var password = PasswordFor(profile, "접속 시험에 필요합니다.");
            if (password == null)
            {
                return;
            }

            session.Status = ConnStatus.Testing;
            session.Result = null;
            _state.Notify(ChangeScope.Session);

            var cts = new CancellationTokenSource();
            _tests[role] = cts;
            try
            {
                var target = ConnectionTarget.From(profile, password);
                var adapter = ResolveAdapter(profile.Kind);
                var readOnly = role == Roles.Source;
                var result = await adapter.TestAsync(target, readOnly, cts.Token).ConfigureAwait(true);
                session.Result = result;
                session.Status = result.Ok ? ConnStatus.Ok : ConnStatus.Error;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                session.Status = ConnStatus.Error;
                session.Result = new ConnectionTestResult { Ok = false, Error = ex.Message, TestedAt = DateTime.Now };
            }
            finally
            {
                _tests.Remove(role);
                _state.Notify(ChangeScope.Session);
            }
        }

        public async Task LoadMetadataAsync(string role)
        {
            ConnectionRoleState session;
            if (!_state.Conn.TryGetValue(role, out session) || session.MetaLoading)
            {
                return;
            }

            if (session.Status != ConnStatus.Ok)
            {
                await TestAsync(role).ConfigureAwait(true);
                if (session.Status != ConnStatus.Ok)
                {
                    return;
                }
            }

            var profile = _state.ProfileForRole(role);
            if (profile == null)
            {
                return;
            }

            var schema = role == Roles.Source ? _state.Job.Source.Schema : _state.Job.Target.Schema;
            session.MetaLoading = true;
            session.MetaError = null;
            _state.Notify(ChangeScope.Session);

            var cts = new CancellationTokenSource();
            _meta[role] = cts;
            try
            {
                var password = PasswordFor(profile, "메타데이터를 불러오려면 비밀번호가 필요합니다.");
                if (password == null)
                {
                    return;
                }

                var target = ConnectionTarget.From(profile, password);
                var adapter = ResolveAdapter(profile.Kind);
                var meta = await adapter.LoadMetadataAsync(target, schema, cts.Token).ConfigureAwait(true);
                session.Metadata = meta;
                MetadataCache.Save(AppServices.DataDirectory, profile.Id, meta);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                session.MetaError = DescribeMetaError(ex);
            }
            finally
            {
                session.MetaLoading = false;
                _meta.Remove(role);
                _state.Notify(ChangeScope.Session);
            }
        }

        public async Task TestAllAsync()
        {
            await Task.WhenAll(TestAsync(Roles.Source), TestAsync(Roles.Target)).ConfigureAwait(true);
            var src = _state.Conn[Roles.Source];
            var tgt = _state.Conn[Roles.Target];
            if (src.Status == ConnStatus.Ok && tgt.Status == ConnStatus.Ok)
            {
                Kit.Toast(_owner, "두 접속 모두 연결됨", "ok");
            }
        }

        public void TryLoadCachedMetadata(string role)
        {
            var profile = _state.ProfileForRole(role);
            if (profile == null)
            {
                return;
            }

            var schema = role == Roles.Source ? _state.Job.Source.Schema : _state.Job.Target.Schema;
            var meta = MetadataCache.TryLoad(AppServices.DataDirectory, profile.Id, schema);
            ConnectionRoleState session;
            if (meta != null && _state.Conn.TryGetValue(role, out session))
            {
                session.Metadata = meta;
            }
        }

        private static IDatabaseAdapter ResolveAdapter(string kind)
        {
            if (AppServices.DatabaseAdapter != null)
            {
                return AppServices.DatabaseAdapter;
            }

            return DatabaseAdapters.For(kind ?? "oracle");
        }

        private static string DescribeMetaError(Exception ex)
        {
            var adapter = ex as AdapterException;
            if (adapter != null && adapter.InnerException != null)
            {
                return adapter.InnerException.Message;
            }

            return ex.Message;
        }
    }
}
