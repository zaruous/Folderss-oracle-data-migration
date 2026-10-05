using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Folderss.Plugins;
using Microsoft.Win32;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Hosting;
using MigrationStudio.Core.Validation;
using System.Collections.Generic;
using MigrationStudio.Core.Jobs;
using MigrationStudio.Core.Mapping;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Storage;
using MigrationStudio.Logic;
using MigrationStudio.Services;
using MigrationStudio.Ui.Modals;
using MigrationStudio.Ui.Pages;
using MigrationStudio.Ui.Settings;

namespace MigrationStudio.Ui
{
    internal sealed class MigrationView : Grid, IShellCommands, IMappingUiHost
    {
        private readonly IPluginManager _manager;
        private readonly StudioState _state;
        private readonly ConnectionService _connections;
        private readonly MappingOperations _mappingOps;
        private readonly SqlEditorHost _sqlEditor;
        private readonly ShellMenu _menu;
        private readonly StepRail _rail;
        private readonly StatusBar _statusBar;
        private readonly ContentControl _pageHost = new ContentControl();
        private ConnectionPage _connectionPage;
        private TablesPage _tablesPage;
        private ColumnsPage _columnsPage;
        private ValidationPage _validationPage;
        private RunPage _runPage;
        private RunPresenter _runPresenter;
        private Window _window;

        public MigrationView(IPluginManager manager)
        {
            _manager = manager;
            AppServices.Manager = manager;
            AppServices.DataDirectory = manager != null ? manager.DataDirectory : null;
            AppServices.PluginDirectory = manager != null ? manager.PluginDirectory : null;

            var loaded = SettingsRepository.Load(manager);
            var job = JobLogic.NewJob(loaded.Settings);
            _state = new StudioState(job);
            _state.ReloadSettings(loaded);
            DraftAutosave.Claim(_state);
            if (DraftAutosave.Owns(_state))
            {
                var draft = JobDraftStore.TryLoad(AppServices.DataDirectory);
                if (draft != null)
                {
                    JobLogic.ResolveConnections(draft.Job, _state.Settings);
                    _state.ReplaceJob(draft.Job, draft.FilePath, draft.Dirty);
                }
            }

            _connections = new ConnectionService(_state, null);
            _mappingOps = new MappingOperations(_state, _connections, null);
            _sqlEditor = new SqlEditorHost(() => this);
            _connections.TryLoadCachedMetadata(Roles.Source);
            _connections.TryLoadCachedMetadata(Roles.Target);
            _state.Changed += OnStateChanged;
            SettingsRepository.Changed += OnSettingsRepositoryChanged;

            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _menu = new ShellMenu(this);
            Grid.SetRow(_menu.View, 0);
            Children.Add(_menu.View);

            var body = new Grid();
            Grid.SetRow(body, 2);
            _rail = new StepRail(_state, GoToStep);
            _rail.SetFootJobDefinitionAction(ShowJobDefinition);
            _rail.SetFootTemplateExportAction(ExportTemplate);
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(_rail, 0);
            body.Children.Add(_rail);
            Grid.SetColumn(_pageHost, 1);
            body.Children.Add(_pageHost);
            Children.Add(body);

            _statusBar = new StatusBar(_state);
            Grid.SetRow(_statusBar, 3);
            Children.Add(_statusBar);

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            GoToStep(0);
            RefreshChrome();
        }

        Window IMappingUiHost.Owner
        {
            get { return _window; }
        }

        StudioState IMappingUiHost.State
        {
            get { return _state; }
        }

        ConnectionService IMappingUiHost.Connections
        {
            get { return _connections; }
        }

        MappingOperations IMappingUiHost.Operations
        {
            get { return _mappingOps; }
        }

        void IMappingUiHost.GoToStep(int step)
        {
            GoToStep(step);
        }

        void IMappingUiHost.GoToColumns(string mappingId)
        {
            _state.Ui.SelMapping = mappingId;
            _state.Ui.SelColumn = null;
            GoToStep(2);
        }

        void IMappingUiHost.OpenSqlEditor(string mappingId)
        {
            OpenSqlEditorInternal(mappingId);
        }

        void IMappingUiHost.OpenAddMapping(string sourceTable, bool sqlMode)
        {
            OpenAddMapping(sourceTable, sqlMode);
        }

        public void OpenAddMapping(string sourceTable, bool sqlMode)
        {
            var dlg = AddMappingDialog.Show(_window, _state, sourceTable, sqlMode);
            ApplyAddMappingResult(dlg);
        }

        void IMappingUiHost.OpenAutoMatch()
        {
            AutoMatchTables();
        }

        void IMappingUiHost.OpenSettings()
        {
            OpenSettings("connections");
        }

        void IMappingUiHost.ExportTemplate()
        {
            ExportTemplate();
        }

        /// <summary>DevHost 캡처용: 검증이 아직 돌고 있는지, 지금까지 받은 항목 수.</summary>
        public bool ValidationRunningForShot
        {
            get { return _state.Pre != null && _state.Pre.Running; }
        }

        public int ValidationItemCountForShot
        {
            get { return _state.Pre != null ? _state.Pre.Items.Count : 0; }
        }

        public int CurrentStep
        {
            get { return _state.CurrentStep; }
        }

        public Window SqlEditorWindowForShot
        {
            get { return _sqlEditor.Window; }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _window = Window.GetWindow(this);
            if (_window != null)
            {
                _connections.SetOwner(_window);
            }

            if (_window != null)
            {
                _window.PreviewKeyDown += OnPreviewKeyDown;
                _window.Closing += OnWindowClosing;
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_window != null)
            {
                _window.PreviewKeyDown -= OnPreviewKeyDown;
                _window.Closing -= OnWindowClosing;
            }

            _sqlEditor.Close();
            SettingsRepository.Changed -= OnSettingsRepositoryChanged;
            _connections.CancelAll();
            if (DraftAutosave.Owns(_state))
            {
                DraftAutosave.FlushNow(_state, AppServices.DataDirectory);
                DraftAutosave.Release(_state);
            }
        }

        private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _sqlEditor.Close();
            if (DraftAutosave.Owns(_state))
            {
                DraftAutosave.FlushNow(_state, AppServices.DataDirectory);
            }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (IsDialogOpen())
            {
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key >= Key.D1 && e.Key <= Key.D5)
                {
                    GoToStep(e.Key - Key.D1);
                    e.Handled = true;
                    return;
                }

                if (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad5)
                {
                    GoToStep(e.Key - Key.NumPad1);
                    e.Handled = true;
                    return;
                }

                switch (e.Key)
                {
                    case Key.O:
                        OpenJob();
                        e.Handled = true;
                        return;
                    case Key.S:
                        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                        {
                            SaveYaml();
                        }
                        else
                        {
                            SaveJob();
                        }

                        e.Handled = true;
                        return;
                    case Key.Q:
                        OpenSqlEditor();
                        e.Handled = true;
                        return;
                }
            }

            if (e.Key == Key.F5)
            {
                StartRun();
                e.Handled = true;
            }
            else if (e.Key == Key.F6)
            {
                RunValidation();
                e.Handled = true;
            }
        }

        private static bool IsDialogOpen()
        {
            foreach (Window w in Application.Current.Windows)
            {
                if (w.IsActive && w != Application.Current.MainWindow && w.Owner != null)
                {
                    return true;
                }
            }

            return false;
        }

        private void NotImplementedToast()
        {
            Kit.Toast(_window, Labels.NextPhaseTooltip, "warn");
        }

        private void OnSettingsRepositoryChanged()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var loaded = SettingsRepository.Load(_manager);
                _state.ReloadSettings(loaded);
            }));
        }

        private void OnStateChanged(ChangeScope scope)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshChrome();
                if (_connectionPage != null && _state.CurrentStep == 0)
                {
                    _connectionPage.RefreshPartial(scope);
                }

                if (_tablesPage != null && _state.CurrentStep == 1)
                {
                    _tablesPage.RefreshPartial(scope);
                }

                if (_columnsPage != null && _state.CurrentStep == 2)
                {
                    _columnsPage.RefreshPartial(scope);
                }

                if (_validationPage != null && _state.CurrentStep == 3 && !_validationPage.IsRunning)
                {
                    _validationPage.RefreshPartial(scope);
                }

                if (_sqlEditor.IsOpen && (scope == ChangeScope.Job || scope == ChangeScope.Session))
                {
                    var w = _sqlEditor.Window;
                    if (w != null && _state.Ui.SelMapping != null)
                    {
                        w.LoadMapping(_state.Ui.SelMapping);
                    }
                }
            }));
        }

        private void RefreshChrome()
        {
            _menu.RefreshJobTitle(_state.Job != null ? _state.Job.JobName : "", _state.Dirty);
            _menu.Refresh();
            _rail.Refresh();
            _statusBar.Refresh();
        }

        public void PrepareForDevHostShot(int step, string openUi, bool fakeOracle)
        {
            if (fakeOracle)
            {
                SeedFakeConnectionSession();
            }

            if (step >= 1 && step <= 5)
            {
                GoToStep(step - 1);
            }

            if (string.Equals(openUi, "columns-sql", StringComparison.OrdinalIgnoreCase))
            {
                var sqlOnly = FindMappingBySource("SQLMAP_MEMBER");
                if (sqlOnly != null)
                {
                    _state.Ui.SelMapping = sqlOnly.Id;
                    GoToStep(2);
                    _columnsPage?.RefreshPartial(ChangeScope.Job);
                }
            }
            else if (step == 3)
            {
                var customer = FindMappingBySource("SRC_CUSTOMER");
                if (customer != null)
                {
                    _state.Ui.SelMapping = customer.Id;
                    _state.Ui.SelColumn = "MOBILE_NO";
                    GoToStep(2);
                    _columnsPage?.RefreshPartial(ChangeScope.Job);
                }
            }

            if (!string.IsNullOrEmpty(openUi))
            {
                switch (openUi)
                {
                    case "sql-editor":
                    case "sql-editor-check":
                        OpenSqlEditorInternal(FindMappingBySource("SQLMAP_MEMBER")?.Id ?? _state.SqlMappings().FirstOrDefault()?.Id);
                        _sqlEditor?.PrepareDevHostTab("check");
                        break;
                    case "sql-editor-alias":
                        OpenSqlEditorInternal(FindMappingBySource("SQLMAP_MEMBER")?.Id);
                        _sqlEditor?.PrepareDevHostTab("alias");
                        break;
                    case "sql-editor-error":
                        OpenSqlEditorInternal(FindMappingBySource("SQLMAP_MEMBER")?.Id);
                        _sqlEditor?.PrepareDevHostErrorLine();
                        break;
                    case "add-mapping":
                        GoToStep(1);
                        OpenAddMapping(null, false);
                        break;
                    case "auto-match":
                        AutoMatchTables();
                        break;
                    case "template-export":
                        ExportTemplate();
                        break;
                }
            }
        }

        private Mapping FindMappingBySource(string source)
        {
            if (_state.Job?.Mappings == null)
            {
                return null;
            }

            foreach (var m in _state.Job.Mappings)
            {
                if (string.Equals(m.Source, source, StringComparison.OrdinalIgnoreCase))
                {
                    return m;
                }
            }

            return null;
        }

        private void SeedFakeConnectionSession()
        {
            var now = DateTime.Now;
            foreach (var role in new[] { Roles.Source, Roles.Target })
            {
                ConnectionRoleState s;
                if (!_state.Conn.TryGetValue(role, out s))
                {
                    continue;
                }

                s.Status = ConnStatus.Ok;
                s.Result = new ConnectionTestResult
                {
                    Ok = true,
                    Version = "Oracle 19c",
                    LatencyMs = 23,
                    TestedAt = now
                };
                if (s.Metadata == null)
                {
                    _connections.TryLoadCachedMetadata(role);
                }
            }

            _connectionPage?.RefreshPartial(ChangeScope.Session);
        }

        public void GoToStep(int step)
        {
            if (step < 0 || step >= Labels.StepTitles.Length)
            {
                return;
            }

            _state.CurrentStep = step;
            switch (step)
            {
                case 0:
                    if (_connectionPage == null)
                    {
                        _connectionPage = new ConnectionPage(_state, _connections, tab => OpenSettings(tab));
                    }

                    _pageHost.Content = _connectionPage;
                    _connectionPage.RefreshPartial(ChangeScope.Job);
                    break;
                case 1:
                    if (_tablesPage == null)
                    {
                        _tablesPage = new TablesPage(this);
                    }

                    _pageHost.Content = _tablesPage;
                    _tablesPage.RefreshPartial(ChangeScope.Job);
                    break;
                case 2:
                    if (_columnsPage == null)
                    {
                        _columnsPage = new ColumnsPage(this);
                    }

                    _pageHost.Content = _columnsPage;
                    _columnsPage.RefreshPartial(ChangeScope.Job);
                    break;
                case 3:
                    if (_validationPage == null)
                    {
                        _validationPage = new ValidationPage(this, CreateValidationService, CreatePostValidation);
                    }

                    _pageHost.Content = _validationPage;
                    _validationPage.RefreshPartial(ChangeScope.Job);
                    break;
                case 4:
                    EnsureRunPage();
                    _pageHost.Content = _runPage;
                    _runPage.RefreshPartial(ChangeScope.Job);
                    break;
                default:
                    _pageHost.Content = new PlaceholderPage(step);
                    break;
            }

            RefreshChrome();
        }

        public void NewJob()
        {
            if (_state.Dirty)
            {
                var choice = Dialogs.AskSaveChanges(_window, _state.Job.JobName, _state.FilePath);
                if (choice == SaveChoice.Cancel)
                {
                    return;
                }

                if (choice == SaveChoice.Save && !SaveJobInternal())
                {
                    return;
                }
            }

            _state.ReplaceJob(JobLogic.NewJob(_state.Settings), null, false);
            GoToStep(0);
        }

        public void OpenJob()
        {
            var dlg = new OpenFileDialog
            {
                Filter = "작업 파일 (*.json;*.yaml;*.yml)|*.json;*.yaml;*.yml|모든 파일 (*.*)|*.*"
            };
            if (dlg.ShowDialog(_window) != true)
            {
                return;
            }

            var ext = Path.GetExtension(dlg.FileName);
            if (ext != null && (ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase) || ext.Equals(".yml", StringComparison.OrdinalIgnoreCase)))
            {
                Dialogs.Show(_window, "열기", "YAML 열기는 아직 지원하지 않습니다. JSON으로 저장한 작업을 여세요.", false);
                return;
            }

            try
            {
                var json = File.ReadAllText(dlg.FileName, Encoding.UTF8);
                var job = JobFile.Parse(json);
                JobLogic.ResolveConnections(job, _state.Settings);

                _state.ReplaceJob(job, dlg.FileName, false);
                _connections.TryLoadCachedMetadata(Roles.Source);
                _connections.TryLoadCachedMetadata(Roles.Target);
                GoToStep(0);
            }
            catch (JobFileException ex)
            {
                Dialogs.Show(_window, "작업 열기", ex.Message, true);
            }
        }

        public void LoadSampleJob()
        {
            _state.ReplaceJob(JobSamples.SampleJob(), null, true);
            _connections.TryLoadCachedMetadata(Roles.Source);
            _connections.TryLoadCachedMetadata(Roles.Target);
            GoToStep(1);
            Kit.Toast(_window, "예제 작업을 불러왔습니다", "ok");
        }

        public void SaveJob()
        {
            SaveJobInternal();
        }

        private bool SaveJobInternal()
        {
            var path = _state.FilePath;
            if (string.IsNullOrEmpty(path))
            {
                var dlg = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = _state.Job.JobName + ".json" };
                if (dlg.ShowDialog(_window) != true)
                {
                    return false;
                }

                path = dlg.FileName;
            }

            try
            {
                var json = JobFile.Serialize(_state.Job, _state.Settings);
                var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Replace(temp, path, null);
                }
                else
                {
                    File.Move(temp, path);
                }

                _state.FilePath = path;
                _state.SetClean();
                Kit.Toast(_window, "저장했습니다: " + Path.GetFileName(path), "ok");
                return true;
            }
            catch (Exception ex)
            {
                Dialogs.Show(_window, "저장", ex.Message, true);
                return false;
            }
        }

        public void SaveYaml()
        {
            var dlg = new SaveFileDialog { Filter = "YAML (*.yaml)|*.yaml", FileName = _state.Job.JobName + ".yaml" };
            if (dlg.ShowDialog(_window) != true)
            {
                return;
            }

            try
            {
                var yaml = JobFile.ToYaml(_state.Job, _state.Settings);
                File.WriteAllText(dlg.FileName, yaml, new UTF8Encoding(false));
                Kit.Toast(_window, "YAML로 저장했습니다: " + Path.GetFileName(dlg.FileName), "ok");
            }
            catch (Exception ex)
            {
                Dialogs.Show(_window, "YAML 저장", ex.Message, true);
            }
        }

        public void ShowJobDefinition()
        {
            JobDefinitionDialog.Show(_window, _state.Job, _state.Settings);
        }

        public void ImportTemplate()
        {
            if (_state.IsRunning)
            {
                return;
            }

            _mappingOps.ImportTemplate();
        }

        public void ExportTemplate()
        {
            if (_state.IsRunning)
            {
                return;
            }

            if (_state.Job.Mappings == null || _state.Job.Mappings.Count == 0)
            {
                Kit.Toast(_window, "내보낼 매핑이 없습니다", "warn");
                return;
            }

            var name = Modals.TemplateExportDialog.Show(_window, TablesLogic.DefaultTemplateName(_state.Job.JobName));
            if (name == null)
            {
                return;
            }

            _mappingOps.ExportTemplate(name);
        }

        public void OpenSettings(string tab)
        {
            if (MigrationSettingsDialog.Show(_window, _state.Job, tab))
            {
                var loaded = SettingsRepository.Load(_manager);
                _state.ReloadSettings(loaded);
            }
        }

        public async void TestAllConnections()
        {
            await _connections.TestAllAsync().ConfigureAwait(true);
        }

        public async void ReloadAllMetadata()
        {
            await Task.WhenAll(_connections.LoadMetadataAsync(Roles.Source), _connections.LoadMetadataAsync(Roles.Target)).ConfigureAwait(true);
        }

        public void AutoMatchTables()
        {
            if (!_mappingOps.HasMetadata())
            {
                Kit.Toast(_window, "메타데이터를 먼저 불러오세요", "warn");
                return;
            }

            var picks = AutoMatchDialog.Show(_window, _state.SourceMeta(), _state.TargetMeta(), _state.Job.Mappings);
            if (picks.Count == 0)
            {
                Kit.Toast(_window, "더 맞출 테이블이 없습니다", "warn");
                return;
            }

            var n = _mappingOps.ApplyAutoMatch(picks);
            Kit.Toast(_window, "테이블 매핑 " + n + "개 추가 · 컬럼 자동 매핑함", "ok");
            GoToStep(1);
        }

        public void AutoMapColumns()
        {
            if (_state.Job.Mappings == null || _state.Job.Mappings.Count == 0)
            {
                Kit.Toast(_window, "매핑이 없습니다", "warn");
                return;
            }

            GoToStep(2);
        }

        public void OpenSqlEditor()
        {
            var sql = _state.SqlMappings();
            if (sql.Count == 0)
            {
                Kit.Toast(_window, "SQL 원본 매핑이 없습니다", "warn");
                return;
            }

            var id = _state.Ui.SelMapping;
            if (string.IsNullOrEmpty(id) || _state.Mapping(id) == null || !_state.Mapping(id).IsSql)
            {
                id = sql[0].Id;
            }

            OpenSqlEditorInternal(id);
        }

        private void OpenSqlEditorInternal(string mappingId)
        {
            _sqlEditor.Open(mappingId);
        }

        public void AddSqlSource()
        {
            var dlg = AddMappingDialog.Show(_window, _state, null, true);
            ApplyAddMappingResult(dlg);
        }

        private void ApplyAddMappingResult(AddMappingDialog.Result dlg)
        {
            if (!dlg.Ok)
            {
                return;
            }

            Mapping m;
            if (dlg.IsSql)
            {
                m = _mappingOps.AddSqlMapping(dlg.SqlName, dlg.Target, dlg.Mode, dlg.StartTable);
                OpenSqlEditorInternal(m.Id);
            }
            else
            {
                m = _mappingOps.AddTableMapping(dlg.SourceTable, dlg.Target, dlg.Mode);
            }

            _state.Ui.SelMapping = m.Id;
            GoToStep(1);
        }

        public void ValidateSql()
        {
            if (_sqlEditor.IsOpen)
            {
                return;
            }

            OpenSqlEditor();
        }

        public void PreviewSql()
        {
            OpenSqlEditor();
        }

        public void RunValidation()
        {
            if (_state.IsRunning)
            {
                Kit.Toast(_window, "실행 중에는 검증할 수 없습니다", "warn");
                return;
            }

            GoToStep(3);
            _validationPage.Start();
        }

        private IValidationService CreateValidationService()
        {
            var factory = AppServices.ValidationServiceFactory;
            if (factory != null)
            {
                return factory(_state, _connections);
            }

            return new ValidationService(_state, _connections);
        }

        private void EnsureRunPage()
        {
            if (_runPresenter == null)
            {
                _runPresenter = new RunPresenter(_state, Dispatcher);
                _runPresenter.StateChanged += () => Dispatcher.BeginInvoke(new Action(RefreshChrome));
                _runPresenter.Render += n => RefreshChrome();
            }

            if (_runPage == null)
            {
                _runPage = new RunPage(this, _runPresenter, CreateRunService);
            }
        }

        private IRunService CreateRunService()
        {
            var factory = AppServices.RunServiceFactory;
            if (factory != null)
            {
                return factory(_state, _connections);
            }

            return new RunService(_state, _connections);
        }

        /// <summary>DevHost 캡처용: 실행 화면의 프레젠터(가짜 에이전트 연결에 쓴다).</summary>
        internal RunPresenter RunPresenterForShot
        {
            get
            {
                EnsureRunPage();
                return _runPresenter;
            }
        }

        /// <summary>DevHost 캡처용: 가짜 에이전트를 연결해 지정한 상태(running · paused · done · stopped · failed)의 실행 화면을 만든다.</summary>
        internal void AttachFakeRunForShot(Func<string, IAgentClient> create, string state)
        {
            GoToStep(4);
            if (state == "idle")
            {
                return;
            }

            var presenter = RunPresenterForShot;
            var st = _state.Job.Strategy;
            presenter.Begin("EXECUTE", st.FetchSize, st.CommitSize, st.Workers);
            var client = create(state);
            presenter.Attach(client, "EXECUTE", false);
            var emit = client.GetType().GetMethod("Emit");
            if (emit != null)
            {
                emit.Invoke(client, null);
            }
        }

        /// <summary>DevHost 캡처용: 실행 시작 흐름의 팝업을 지정해 띄운다.</summary>
        internal void ShowRunPopupForShot(string kind)
        {
            var target = _state.ProfileForRole(Roles.Target);
            var schema = _state.Job.Target != null ? _state.Job.Target.Schema : "";
            switch (kind)
            {
                case "run-popup-destructive":
                    Modals.RunDialogs.ConfirmDestructive(_window, target, schema, new List<string> { "TRUNCATE + INSERT  " + schema + ".TB_MEMBER_GRADE" });
                    break;
                case "run-popup-blocked":
                    var gate = new GateResult { Blocked = true, Errors = 1 };
                    gate.Blocking.Add(new ValidationItem { Check = "NOT NULL", Target = "TB_SALES_ORDER.CHANNEL_CD", Level = "ERROR", Detail = "NOT NULL 컬럼에 값이 없음 — 모든 행이 ORA-01400으로 거부됨" });
                    Modals.RunDialogs.ShowBlocked(_window, gate);
                    break;
                case "run-popup-validate-first":
                    Modals.RunDialogs.AskValidateFirst(_window);
                    break;
                case "run-popup-stale":
                    Modals.RunDialogs.ConfirmStale(_window);
                    break;
            }
        }

        private MigrationStudio.Core.Validation.IPostValidationRunner CreatePostValidation()
        {
            var factory = AppServices.PostValidationFactory;
            if (factory != null)
            {
                return factory(_state, _connections);
            }

            return new PostValidationService(_state, _connections);
        }

        public void RunPostValidation()
        {
            _state.Ui.ValTab = "post";
            GoToStep(3);
            _validationPage.StartPost();
        }

        public void DryRun()
        {
            StartWithMode("DRY");
        }

        public void StartRun()
        {
            StartWithMode(null);
        }

        private void StartWithMode(string mode)
        {
            GoToStep(4);
            _runPage.StartRun(mode);
        }

        public void PauseRun()
        {
            EnsureRunPage();
            _runPresenter.Pause();
        }

        public void ResumeRun()
        {
            EnsureRunPage();
            _runPresenter.Resume();
        }

        public void StopRun()
        {
            EnsureRunPage();
            _runPresenter.Stop();
        }

        public void ResumeFromCheckpoint()
        {
            _state.Ui.RunMode = "RESUME";
            StartWithMode("RESUME");
        }

        public void ShowShortcuts()
        {
            Dialogs.Show(_window, "단축키", "Ctrl+1…5 단계 · Ctrl+O 열기 · Ctrl+S 저장 · Ctrl+Shift+S YAML · Ctrl+Q SQL 편집기", false);
        }

        public void ShowAbout()
        {
            var agent = AppServices.DataDirectory != null
                ? Path.Combine(AppServices.DataDirectory, "agent")
                : "(미설정)";
            var asmVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            var msg = "Migration Studio\n버전 " + (asmVersion != null ? asmVersion.ToString(3) : "") + "\n플러그인 id:zaruous.folderss-oracle-migration\n에이전트: " + agent + "\n데이터: " + (AppServices.DataDirectory ?? "");
            Dialogs.Show(_window, "Migration Studio 정보", msg, false);
        }

        private bool HasResumableCheckpoint()
        {
            var cps = _state.Job.Checkpoints;
            return cps != null && cps.Values.Any(c => c != null && c.Status != "done");
        }

        public ShellCommandState CommandState()
        {
            var hasMeta = _mappingOps.HasMetadata();
            var hasMap = _state.Job.Mappings != null && _state.Job.Mappings.Count > 0;
            var hasSql = _state.SqlMappings().Count > 0;
            return new ShellCommandState
            {
                CanTestConnections = true,
                CanReloadMeta = _state.ProfileForRole(Roles.Source) != null && _state.ProfileForRole(Roles.Target) != null,
                CanValidation = hasMeta && hasMap && !_state.IsRunning && (_validationPage == null || !_validationPage.IsRunning),
                CanStartRun = hasMeta && hasMap && !_state.IsRunning,
                CanPause = _state.Run != null && _state.Run.State == RunStates.Running,
                CanStop = _state.Run != null && (_state.Run.State == RunStates.Running || _state.Run.State == RunStates.Pausing || _state.Run.State == RunStates.Paused),
                CanResume = _state.Run != null && _state.Run.State == RunStates.Paused,
                CanResumeCheckpoint = !_state.IsRunning && HasResumableCheckpoint(),
                CanPostValidation = _state.Run != null && (_state.Run.State == RunStates.Done || _state.Run.State == RunStates.Stopped || _state.Run.State == RunStates.Failed),
                CanSqlEditor = hasSql,
                CanAutoMap = hasMap,
                CanImportExport = hasMap && !_state.IsRunning
            };
        }
    }
}
