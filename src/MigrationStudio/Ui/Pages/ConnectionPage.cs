using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using MigrationStudio.Logic;
using MigrationStudio.Services;

namespace MigrationStudio.Ui.Pages
{
    internal sealed class ConnectionPage : PageFrame
    {
        private readonly StudioState _state;
        private readonly ConnectionService _connections;
        private readonly Action<string> _openSettings;
        private readonly Grid _cardsGrid = new Grid();
        private ComboBox _sourceCombo;
        private ComboBox _targetCombo;
        private TextBox _sourceSchema;
        private TextBox _targetSchema;
        private StackPanel _sourceNotices;
        private StackPanel _targetNotices;
        private StackPanel _sourceSummary;
        private StackPanel _targetSummary;
        private StackPanel _sourceStatus;
        private StackPanel _targetStatus;
        private StackPanel _strategyHost;
        private Button _sourceMetaBtn;
        private Button _targetMetaBtn;
        private bool _suppressCombo;

        public ConnectionPage(StudioState state, ConnectionService connections, Action<string> openSettings)
        {
            _state = state;
            _connections = connections;
            _openSettings = openSettings;
            SetStep(0, Labels.StepTitles[0], "원본·대상 DB 접속과 스키마, 이관 전략을 정합니다.");
            WireHeader();
            SetBody(BuildBody());
            SetFooterHint("접속은 이 플러그인의 설정에만 저장합니다(DB Helper와 공유하지 않음).");
            RefreshPartial(ChangeScope.Job);
        }

        private void WireHeader()
        {
            var settingsBtn = Kit.Button("마이그레이션 설정…", Icons.Setting);
            settingsBtn.Click += (s, e) => { if (_openSettings != null) _openSettings("connections"); };
            var testBtn = Kit.PrimaryButton("두 접속 모두 테스트", Icons.Link);
            testBtn.Click += async (s, e) => await _connections.TestAllAsync().ConfigureAwait(true);
            SetHeaderActions(settingsBtn, testBtn);
        }

        private UIElement BuildBody()
        {
            var root = new StackPanel();
            _cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            _cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _cardsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var arrow = BuildArrow();
            Grid.SetColumn(arrow, 1);
            Grid.SetRow(arrow, 0);
            _cardsGrid.Children.Add(arrow);
            BuildRoleCard(Roles.Source, true);
            BuildRoleCard(Roles.Target, false);
            root.Children.Add(_cardsGrid);
            root.Children.Add(BuildStrategyCard());
            return root;
        }

        private UIElement BuildArrow()
        {
            var arrow = new TextBlock
            {
                Text = Icons.Arrow,
                FontFamily = Theme.IconFont,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            arrow.SetResourceReference(TextBlock.ForegroundProperty, Theme.Accent);
            var inner = Kit.AccentTint(arrow);
            inner.Width = 30;
            inner.Height = 30;
            inner.CornerRadius = new CornerRadius(15);
            inner.HorizontalAlignment = HorizontalAlignment.Center;
            inner.VerticalAlignment = VerticalAlignment.Center;
            return inner;
        }

        private UIElement BuildRoleCard(string role, bool isSource)
        {
            var col = isSource ? 0 : 2;
            var notices = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            if (isSource)
            {
                _sourceNotices = notices;
            }
            else
            {
                _targetNotices = notices;
            }

            var combo = new ComboBox { MinWidth = 200, IsEditable = false };
            if (isSource)
            {
                _sourceCombo = combo;
            }
            else
            {
                _targetCombo = combo;
            }

            combo.SelectionChanged += (s, e) => OnProfileChanged(role, combo);
            var manage = Kit.Button("접속 관리…", Icons.Setting);
            manage.Click += (s, e) => { if (_openSettings != null) _openSettings("connections"); };
            manage.Margin = new Thickness(8, 0, 0, 0);
            manage.HorizontalAlignment = HorizontalAlignment.Right;
            var comboRow = new Grid();
            comboRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            comboRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(combo, 0);
            Grid.SetColumn(manage, 1);
            combo.HorizontalAlignment = HorizontalAlignment.Stretch;
            comboRow.Children.Add(combo);
            comboRow.Children.Add(manage);

            var schema = new TextBox { FontFamily = Theme.Mono, FontSize = 12, FontWeight = FontWeights.SemiBold };
            if (isSource)
            {
                _sourceSchema = schema;
            }
            else
            {
                _targetSchema = schema;
            }

            schema.LostFocus += (s, e) => OnSchemaLostFocus(role, schema);

            var summaryHost = new StackPanel();
            if (isSource)
            {
                _sourceSummary = summaryHost;
            }
            else
            {
                _targetSummary = summaryHost;
            }

            var status = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            if (isSource)
            {
                _sourceStatus = status;
            }
            else
            {
                _targetStatus = status;
            }

            var testBtn = Kit.Button("접속 테스트", Icons.Link);
            testBtn.Margin = new Thickness(0, 0, 8, 0);
            testBtn.Click += (s, e) => _ = _connections.TestAsync(role);
            var metaBtn = Kit.Button("메타데이터 불러오기", Icons.Sync);
            metaBtn.Click += async (s, e) => await _connections.LoadMetadataAsync(role).ConfigureAwait(true);
            if (isSource)
            {
                _sourceMetaBtn = metaBtn;
            }
            else
            {
                _targetMetaBtn = metaBtn;
            }

            var footRow = new StackPanel();
            var footButtons = new WrapPanel();
            footButtons.Children.Add(testBtn);
            footButtons.Children.Add(metaBtn);
            footRow.Children.Add(footButtons);
            footRow.Children.Add(status);

            var body = new StackPanel();
            body.Children.Add(Kit.Field("접속 (마이그레이션 설정)", comboRow, true, null));
            body.Children.Add(notices);
            body.Children.Add(summaryHost);
            body.Children.Add(Kit.Field("스키마", schema, true, "이 작업에서 읽고/쓸 스키마. 비우면 접속의 기본 스키마"));

            var head = new DockPanel();
            var headLeft = new StackPanel { Orientation = Orientation.Horizontal };
            headLeft.Children.Add(Kit.RoleTag(isSource));
            var roleName = Theme.Text(isSource ? "원본" : "대상");
            roleName.FontWeight = FontWeights.SemiBold;
            roleName.Margin = new Thickness(8, 0, 0, 0);
            headLeft.Children.Add(roleName);
            DockPanel.SetDock(headLeft, Dock.Left);
            head.Children.Add(headLeft);
            var headRight = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var profile = _state.ProfileForRole(role);
            if (profile != null)
            {
                headRight.Children.Add(Kit.DbBadge(profile));
            }

            if (isSource)
            {
                var ro = Kit.Tag("읽기 전용", "ro");
                ro.Margin = new Thickness(6, 0, 0, 0);
                ro.ToolTip = "원본 세션은 SET TRANSACTION READ ONLY로 엽니다";
                headRight.Children.Add(ro);
            }

            DockPanel.SetDock(headRight, Dock.Right);
            head.Children.Add(headRight);

            var card = Kit.Card("", head, body, footRow);
            Grid.SetColumn(card, col);
            _cardsGrid.Children.Add(card);
            return card;
        }

        private UIElement BuildStrategyCard()
        {
            _strategyHost = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            RebuildStrategy();
            var reset = Kit.GhostButton("설정 기본값으로", null);
            reset.Click += (s, e) =>
            {
                if (_state.Settings != null && _state.Job != null)
                {
                    MigrationSettingsStore.ApplyDefaults(_state.Job.Strategy, _state.Settings.Defaults);
                    _state.MarkChanged();
                    RebuildStrategy();
                }
            };
            var head = new DockPanel();
            var headText = new StackPanel();
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock { Text = Icons.Setting, FontFamily = Theme.IconFont, Margin = new Thickness(0, 0, 6, 0) });
            var title = Theme.Text("이관 전략");
            title.FontWeight = FontWeights.SemiBold;
            titleRow.Children.Add(title);
            headText.Children.Add(titleRow);
            var sub = Theme.Secondary("이 작업의 기본값(새 작업은 마이그레이션 설정의 기본값으로 시작) — SQL 원본은 자기 Fetch·커밋 크기를 따로 가질 수 있음");
            sub.TextWrapping = TextWrapping.Wrap;
            sub.Margin = new Thickness(0, 4, 0, 0);
            headText.Children.Add(sub);
            DockPanel.SetDock(headText, Dock.Left);
            head.Children.Add(headText);
            reset.HorizontalAlignment = HorizontalAlignment.Right;
            DockPanel.SetDock(reset, Dock.Right);
            head.Children.Add(reset);
            return Kit.Card("", head, _strategyHost, null);
        }

        private void RebuildStrategy()
        {
            if (_strategyHost == null || _state.Job == null)
            {
                return;
            }

            _strategyHost.Children.Clear();
            var s = _state.Job.Strategy ?? new MigrationStrategy();
            var modes = new List<Kit.SegmentOption>
            {
                new Kit.SegmentOption { Value = ExecutionModes.Full, Label = "전체 이관" },
                new Kit.SegmentOption { Value = ExecutionModes.Incremental, Label = "증분 이관" },
                new Kit.SegmentOption { Value = ExecutionModes.Cdc, Label = "CDC" }
            };
            _strategyHost.Children.Add(Kit.FormGrid(2,
                Kit.Field("실행 방식", Kit.Segmented(modes, s.Mode, v => { s.Mode = v; _state.MarkChanged(); RebuildStrategy(); }), false, ModeHint(s.Mode)),
                Kit.Field("트랜잭션 단위", CommitCombo(s), false, null),
                Kit.Field("Fetch 크기", FetchCombo(s), false, null),
                Kit.Field("병렬 작업자", WorkersSegment(s), false, "체크포인트 키 범위를 나눠 작업자마다 따로 읽고 씁니다(세션 " + s.Workers.ToString(CultureInfo.InvariantCulture) + "개)")));
            if (s.Mode == ExecutionModes.Cdc)
            {
                _strategyHost.Children.Add(Kit.FormGrid(2,
                    Kit.Field("동기화 주기", PollCombo(s), false, "주기마다 워터마크 다음 행을 읽어 반영합니다. 동기화 중 병렬 작업자는 1로 돕니다."),
                    Kit.Field("최대 실행 시간", MaxRunCombo(s), false, s.MaxRunHours > 0
                        ? "지나면 자동 종료합니다. 다시 시작하면 워터마크 다음부터 이어 갑니다."
                        : "무기한: 중지 버튼을 누를 때까지 돕니다. 잊으면 계속 쓰므로 종료 시간을 두는 편이 안전합니다.")));
            }

            if (s.Mode == ExecutionModes.Cdc || s.Mode == ExecutionModes.Incremental)
            {
                _strategyHost.Children.Add(Kit.Field("지연 창", LagCombo(s), false,
                    "체크포인트 열이 날짜·시각이면 원본 시각에서 이만큼 뺀 시각까지만 읽습니다. 수정시각은 과거인데 커밋이 늦은 행을 놓치지 않기 위한 여유로, 가장 긴 트랜잭션보다 길게 두되 그만큼 반영이 늦어집니다."));
            }
            var prefixBox = new TextBox { Width = 92, FontFamily = Theme.Mono, Text = s.ErrorTable ?? "" };
            prefixBox.TextChanged += (a, b) => { s.ErrorTable = prefixBox.Text; _state.MarkChanged(); };
            var errItems = new List<Kit.RadioCardItem>
            {
                new Kit.RadioCardItem { Value = ErrorPolicies.Continue, Title = ErrorPolicies.Label(ErrorPolicies.Continue), Description = "오류 행은 오류 테이블에 남기고 계속", Extra = prefixBox },
                new Kit.RadioCardItem { Value = ErrorPolicies.Stop, Title = ErrorPolicies.Label(ErrorPolicies.Stop), Description = "오류 시 즉시 중지" },
                new Kit.RadioCardItem { Value = ErrorPolicies.Retry, Title = ErrorPolicies.Label(ErrorPolicies.Retry), Description = "일시 오류 3회 재시도" }
            };
            _strategyHost.Children.Add(Kit.Field("오류 처리", Kit.RadioCards("err", errItems, s.ErrorPolicy, v => { s.ErrorPolicy = v; _state.MarkChanged(); RebuildStrategy(); }), false, null));
        }

        private static string ModeHint(string mode)
        {
            if (mode == ExecutionModes.Incremental)
            {
                return "매핑의 체크포인트 열 기준으로 지난 실행의 마지막 키(워터마크) 다음 행만 읽습니다. 쓰기 방식은 INSERT+UPDATE를 권장합니다.";
            }

            if (mode == ExecutionModes.Cdc)
            {
                return "수정시각·증가 키(체크포인트 열) 기준으로 주기마다 추가·변경을 반영합니다. 삭제는 따라가지 않습니다(옵션은 다음 단계).";
            }

            return "전체 테이블/쿼리를 처음부터 이관합니다.";
        }

        private ComboBox PollCombo(MigrationStrategy s)
        {
            var choices = new[] { 30, 60, 300, 900, 3600 };
            var box = new ComboBox();
            foreach (var n in choices)
            {
                box.Items.Add(n < 60 ? n + "초" : n / 60 + "분");
            }

            var idx = Array.IndexOf(choices, s.PollIntervalSeconds);
            if (idx < 0)
            {
                box.Items.Add(s.PollIntervalSeconds + "초");
                idx = box.Items.Count - 1;
            }

            box.SelectedIndex = idx;
            box.SelectionChanged += (a, b) =>
            {
                if (box.SelectedIndex >= 0 && box.SelectedIndex < choices.Length)
                {
                    s.PollIntervalSeconds = choices[box.SelectedIndex];
                    _state.MarkChanged();
                }
            };
            return box;
        }

        private ComboBox LagCombo(MigrationStrategy s)
        {
            var choices = new[] { 0, 60, 300, 900, 1800, 3600 };
            var box = new ComboBox();
            foreach (var n in choices)
            {
                box.Items.Add(n == 0 ? "없음(원본 현재 시각까지)" : n < 60 ? n + "초" : n / 60 + "분");
            }

            var idx = Array.IndexOf(choices, s.LagSeconds);
            if (idx < 0)
            {
                box.Items.Add(s.LagSeconds + "초");
                idx = box.Items.Count - 1;
            }

            box.SelectedIndex = idx;
            box.SelectionChanged += (a, b) =>
            {
                if (box.SelectedIndex >= 0 && box.SelectedIndex < choices.Length)
                {
                    s.LagSeconds = choices[box.SelectedIndex];
                    _state.MarkChanged();
                }
            };
            return box;
        }

        private ComboBox MaxRunCombo(MigrationStrategy s)
        {
            var choices = new[] { 1, 8, 24, 72, 0 };
            var box = new ComboBox();
            foreach (var n in choices)
            {
                box.Items.Add(n == 0 ? "무기한(중지할 때까지)" : n + "시간");
            }

            var idx = Array.IndexOf(choices, s.MaxRunHours);
            if (idx < 0)
            {
                box.Items.Add(s.MaxRunHours + "시간");
                idx = box.Items.Count - 1;
            }

            box.SelectedIndex = idx;
            box.SelectionChanged += (a, b) =>
            {
                if (box.SelectedIndex >= 0 && box.SelectedIndex < choices.Length)
                {
                    s.MaxRunHours = choices[box.SelectedIndex];
                    _state.MarkChanged();
                    RebuildStrategy();
                }
            };
            return box;
        }

        private ComboBox CommitCombo(MigrationStrategy s)
        {
            var box = new ComboBox();
            foreach (var n in new[] { 1000, 10000, 50000 })
            {
                box.Items.Add(n.ToString("N0", CultureInfo.GetCultureInfo("en-US")) + " rows / commit");
            }

            box.SelectedIndex = s.CommitSize == 1000 ? 0 : s.CommitSize == 50000 ? 2 : 1;
            box.SelectionChanged += (a, b) =>
            {
                var idx = box.SelectedIndex;
                s.CommitSize = idx == 0 ? 1000 : idx == 2 ? 50000 : 10000;
                _state.MarkChanged();
            };
            return box;
        }

        private ComboBox FetchCombo(MigrationStrategy s)
        {
            var box = new ComboBox();
            foreach (var n in new[] { 1000, 5000, 10000 })
            {
                box.Items.Add(n.ToString("N0", CultureInfo.GetCultureInfo("en-US")) + " rows / fetch");
            }

            box.SelectedIndex = s.FetchSize == 1000 ? 0 : s.FetchSize == 10000 ? 2 : 1;
            box.SelectionChanged += (a, b) =>
            {
                var idx = box.SelectedIndex;
                s.FetchSize = idx == 0 ? 1000 : idx == 2 ? 10000 : 5000;
                _state.MarkChanged();
            };
            return box;
        }

        private UIElement WorkersSegment(MigrationStrategy s)
        {
            var opts = new List<Kit.SegmentOption>();
            foreach (var n in new[] { 1, 2, 4, 8 })
            {
                opts.Add(new Kit.SegmentOption { Value = n.ToString(CultureInfo.InvariantCulture), Label = n.ToString(CultureInfo.InvariantCulture) });
            }

            return Kit.Segmented(opts, s.Workers.ToString(CultureInfo.InvariantCulture), v =>
            {
                int w;
                if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out w))
                {
                    s.Workers = w;
                    _state.MarkChanged();
                }
            });
        }

        public void RefreshPartial(ChangeScope scope)
        {
            if (scope == ChangeScope.Settings || scope == ChangeScope.Job)
            {
                FillCombo(_sourceCombo, Roles.Source);
                FillCombo(_targetCombo, Roles.Target);
            }

            if (scope == ChangeScope.Session || scope == ChangeScope.Job || scope == ChangeScope.Settings)
            {
                RefreshNotices(Roles.Source, _sourceNotices);
                RefreshNotices(Roles.Target, _targetNotices);
                RefreshSummary(Roles.Source, _sourceSummary);
                RefreshSummary(Roles.Target, _targetSummary);
                RefreshStatus(Roles.Source, _sourceStatus);
                RefreshStatus(Roles.Target, _targetStatus);
                UpdateMetaButtons();
            }

            if (scope == ChangeScope.Job || scope == ChangeScope.Settings)
            {
                if (_sourceSchema != null)
                {
                    _sourceSchema.Text = _state.Job.Source != null ? _state.Job.Source.Schema ?? "" : "";
                }

                if (_targetSchema != null)
                {
                    _targetSchema.Text = _state.Job.Target != null ? _state.Job.Target.Schema ?? "" : "";
                }
            }
        }

        private void UpdateMetaButtons()
        {
            UpdateMetaButton(Roles.Source, _sourceMetaBtn);
            UpdateMetaButton(Roles.Target, _targetMetaBtn);
        }

        private void UpdateMetaButton(string role, Button btn)
        {
            if (btn == null)
            {
                return;
            }

            ConnectionRoleState session;
            if (!_state.Conn.TryGetValue(role, out session))
            {
                return;
            }

            var label = session.Metadata != null ? "메타데이터 다시 불러오기" : "메타데이터 불러오기";
            btn.Content = label;
            // 글자를 바꾸면 UI 자동화 이름도 같이 — 아니면 화면 낭독기·자동 검사가 두 버튼을 같은 이름으로 본다
            Kit.NameForAutomation(btn, label);
        }

        private void RefreshSummary(string role, StackPanel host)
        {
            if (host == null)
            {
                return;
            }

            host.Children.Clear();
            var profile = _state.ProfileForRole(role);
            if (profile == null)
            {
                return;
            }

            var pwd = profile.SavePassword && !string.IsNullOrEmpty(profile.ProtectedPassword)
                ? "저장됨(DPAPI)"
                : "연결할 때 입력";
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AddSummaryRow(grid, 0, "DB 종류", "Oracle", false);
            AddSummaryRow(grid, 1, "주소", ConnectionLogic.Address(profile), true);
            AddSummaryRow(grid, 2, "사용자", profile.User ?? "", true);
            AddSummaryRow(grid, 3, "비밀번호", pwd, false);
            var colorVal = new StackPanel { Orientation = Orientation.Horizontal };
            colorVal.Children.Add(Kit.DbBadge(profile));
            if (profile.WriteBlocked)
            {
                var wb = Kit.Tag("쓰기 금지", "ro");
                wb.Margin = new Thickness(6, 0, 0, 0);
                colorVal.Children.Add(wb);
            }

            AddSummaryRow(grid, 4, "색 표시", colorVal);
            var box = new Border
            {
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 8),
                Child = grid
            };
            box.SetResourceReference(Border.BackgroundProperty, Theme.SurfaceBackground);
            box.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            host.Children.Add(box);
        }

        private static void AddSummaryRow(Grid grid, int row, string key, string value, bool mono)
        {
            while (grid.RowDefinitions.Count <= row)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            var k = Theme.Secondary(key);
            k.FontSize = 12;
            k.Margin = new Thickness(0, row > 0 ? 4 : 0, 12, 0);
            var v = Theme.Text(value ?? "");
            v.FontSize = 12;
            v.TextWrapping = TextWrapping.Wrap;
            v.Margin = new Thickness(0, row > 0 ? 4 : 0, 0, 0);
            if (mono)
            {
                v.FontFamily = Theme.Mono;
                v.FontWeight = FontWeights.SemiBold;
            }

            Grid.SetRow(k, row);
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            grid.Children.Add(k);
            grid.Children.Add(v);
        }

        private static void AddSummaryRow(Grid grid, int row, string key, UIElement value)
        {
            while (grid.RowDefinitions.Count <= row)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            var k = Theme.Secondary(key);
            k.FontSize = 12;
            k.Margin = new Thickness(0, row > 0 ? 4 : 0, 12, 0);
            Grid.SetRow(k, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            if (value is FrameworkElement fe)
            {
                fe.Margin = new Thickness(0, row > 0 ? 4 : 0, 0, 0);
            }

            grid.Children.Add(k);
            grid.Children.Add(value);
        }

        private void FillCombo(ComboBox combo, string role)
        {
            if (combo == null)
            {
                return;
            }

            var selected = role == Roles.Source ? _state.Job.Source.ProfileId : _state.Job.Target.ProfileId;
            _suppressCombo = true;
            combo.Items.Clear();
            combo.Items.Add(Labels.ComboPlaceholder);
            if (_state.Settings != null)
            {
                foreach (var p in _state.Settings.Connections)
                {
                    var item = new ComboBoxItem
                    {
                        Content = ConnectionLogic.ComboItemText(p),
                        Tag = p,
                        IsEnabled = role != Roles.Target || !p.WriteBlocked
                    };
                    combo.Items.Add(item);
                    if (string.Equals(p.Id, selected, StringComparison.Ordinal))
                    {
                        combo.SelectedItem = item;
                    }
                }
            }

            if (combo.SelectedIndex < 0)
            {
                combo.SelectedIndex = 0;
            }

            _suppressCombo = false;
        }

        private void OnProfileChanged(string role, ComboBox combo)
        {
            if (_suppressCombo)
            {
                return;
            }

            var item = combo.SelectedItem as ComboBoxItem;
            var profile = item != null ? item.Tag as ConnectionProfile : null;
            if (profile == null)
            {
                return;
            }

            JobLogic.ApplyProfileSelection(_state.Job, role, profile);
            _state.ClearSessionForRole(role);
            _connections.TryLoadCachedMetadata(role);
            _state.MarkChanged();
            Kit.Toast(Window.GetWindow(this), profile.Name + " 접속을 골랐습니다 · 메타데이터를 불러오세요", "ok");
            RefreshPartial(ChangeScope.Session);
        }

        private void OnSchemaLostFocus(string role, TextBox box)
        {
            var reference = role == Roles.Source ? _state.Job.Source : _state.Job.Target;
            var upper = (box.Text ?? "").Trim().ToUpperInvariant();
            if (string.Equals(reference.Schema, upper, StringComparison.Ordinal))
            {
                return;
            }

            reference.Schema = upper;
            _state.ClearSessionForRole(role);
            _connections.TryLoadCachedMetadata(role);
            _state.MarkChanged();
            RefreshPartial(ChangeScope.Session);
        }

        private void RefreshNotices(string role, StackPanel host)
        {
            if (host == null)
            {
                return;
            }

            host.Children.Clear();
            var missing = JobLogic.Missing(_state.Job, _state.Settings, role);
            var profile = _state.ProfileForRole(role);
            var kind = ConnectionLogic.NoticeKind(role, profile, missing);
            if (kind == ConnectionNoticeKind.MissingProfile && missing != null)
            {
                var inlines = new List<Inline>
                {
                    new Run("이 PC의 마이그레이션 설정에 "),
                    new Run(missing.Name) { FontWeight = FontWeights.SemiBold },
                    new Run(" (" + (missing.Host ?? "") + "/" + (missing.Service ?? "") + ") 접속이 없습니다. ")
                };
                host.Children.Add(Kit.Notice("warn", inlines));
            }
            else if (kind == ConnectionNoticeKind.TargetWriteBlocked)
            {
                host.Children.Add(Kit.Notice("err", new List<Inline> { new Run((profile != null ? profile.Name : "") + "은(는) \"쓰기 금지\" 접속이라 대상으로 쓸 수 없습니다.") }));
            }
            else if (kind == ConnectionNoticeKind.TargetRedColor)
            {
                host.Children.Add(Kit.Notice("warn", new List<Inline> { new Run("운영 DB에 씁니다. TRUNCATE·DELETE 방식은 실행 직전에 확인 체크를 한 번 더 받습니다.") }));
            }
        }

        private void RefreshStatus(string role, StackPanel host)
        {
            if (host == null)
            {
                return;
            }

            host.Children.Clear();
            ConnectionRoleState session;
            if (!_state.Conn.TryGetValue(role, out session))
            {
                return;
            }

            var line = ConnectionLogic.TestStatusLine(ToSession(session), session.Result);
            if (line == "testing")
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(Kit.Spinner());
                row.Children.Add(Theme.Secondary(" 연결하는 중…"));
                host.Children.Add(row);
            }
            else if (line.StartsWith("ok|", StringComparison.Ordinal))
            {
                var parts = line.Split('|');
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                row.Children.Add(Kit.Pill("ok", parts.Length > 1 ? parts[1] : "Connected", Icons.Check));
                if (parts.Length > 2)
                {
                    var ver = Theme.Text(" " + parts[2]);
                    ver.FontWeight = FontWeights.SemiBold;
                    row.Children.Add(ver);
                }

                if (parts.Length > 3)
                {
                    row.Children.Add(Theme.Secondary(" " + parts[3] + " ms"));
                }

                if (parts.Length > 4)
                {
                    var at = Theme.Secondary(" " + parts[4]);
                    at.SetResourceReference(TextBlock.ForegroundProperty, Theme.DisabledText);
                    row.Children.Add(at);
                }

                host.Children.Add(row);
            }
            else if (line.StartsWith("error|", StringComparison.Ordinal))
            {
                host.Children.Add(Kit.Notice("err", new List<Inline> { new Run(line.Substring(6)) }));
            }
            else
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(Kit.Dot("unknown"));
                row.Children.Add(Theme.Secondary(" 이번 창에서 아직 시험하지 않음"));
                host.Children.Add(row);
            }

            var meta = session.Metadata;
            var metaLine = ConnectionLogic.MetaStatusLine(meta, ToSession(session), session.MetaLoading);
            if (metaLine == "loading")
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(Kit.Spinner());
                row.Children.Add(Theme.Secondary(" 메타데이터 불러오는 중…"));
                host.Children.Add(row);
            }
            else if (!metaLine.StartsWith("error|", StringComparison.Ordinal) && metaLine != "none" && metaLine != "loading")
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                var icon = new TextBlock
                {
                    Text = Icons.Table,
                    FontFamily = Theme.IconFont,
                    Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.Children.Add(icon);
                var paren = metaLine.IndexOf(" (", StringComparison.Ordinal);
                if (paren > 0)
                {
                    row.Children.Add(Theme.Text(metaLine.Substring(0, paren)));
                    var suffix = Theme.Secondary(metaLine.Substring(paren));
                    suffix.SetResourceReference(TextBlock.ForegroundProperty, Theme.DisabledText);
                    row.Children.Add(suffix);
                }
                else
                {
                    row.Children.Add(Theme.Text(metaLine));
                }

                host.Children.Add(row);
            }
            else if (metaLine.StartsWith("error|", StringComparison.Ordinal))
            {
                host.Children.Add(Kit.Notice("err", new List<Inline> { new Run(metaLine.Substring(6)) }));
            }
            else if (metaLine == "none")
            {
                host.Children.Add(Kit.Notice("warn", new List<Inline> { new Run("메타데이터를 불러오지 않았습니다") }));
            }
        }

        private static ConnectionSession ToSession(ConnectionRoleState state)
        {
            return new ConnectionSession { Status = state.Status, MetaLoading = state.MetaLoading, MetaError = state.MetaError };
        }
    }
}
