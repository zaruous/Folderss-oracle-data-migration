using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Mapping;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Sql;
using MigrationStudio.Logic;
using MigrationStudio.Services;

namespace MigrationStudio.Ui
{
    internal sealed class SqlSourceEditorWindow : Window
    {
        private readonly IMappingUiHost _host;
        private readonly ComboBox _mappingPicker;
        private readonly TextBox _nameBox;
        private readonly TextBox _sqlBox;
        private readonly StackPanel _lineNumbers;
        private readonly StackPanel _resultPanel;
        private readonly TextBlock _banner;
        private readonly Border _bannerHost = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        private readonly StackPanel _rightPanel = new StackPanel();
        private ScrollViewer _gutterScroll;
        private readonly TextBlock _caret;
        private readonly Border _workspace;
        private Mapping _mapping;
        private CancellationTokenSource _requestCts;
        private string _tab = "check";
        private bool _loading;

        public SqlSourceEditorWindow(IMappingUiHost host)
        {
            _host = host;
            Title = "SQL 원본 편집기";
            Width = 1080;
            Height = 720;
            MinWidth = 760;
            MinHeight = 520;
            ShowInTaskbar = false;
            Theme.ApplyWindow(this);

            var root = new DockPanel { Margin = new Thickness(12) };
            var top = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _mappingPicker = new ComboBox { MinWidth = 300, Height = 28 };
            _mappingPicker.SelectionChanged += (s, e) =>
            {
                var id = _mappingPicker.SelectedValue as string;
                if (!string.IsNullOrEmpty(id) && (_mapping == null || !string.Equals(_mapping.Id, id, StringComparison.Ordinal)))
                {
                    LoadMapping(id);
                }
            };
            top.Children.Add(_mappingPicker);
            var add = Kit.Button("SQL 원본 추가", Icons.Add);
            add.Margin = new Thickness(6, 0, 0, 0);
            add.Click += (s, e) => { Hide(); _host.OpenAddMapping(null, true); Show(); RefreshPicker(); };
            Grid.SetColumn(add, 1);
            top.Children.Add(add);
            var delete = Kit.GhostButton("삭제", Icons.Delete);
            delete.Margin = new Thickness(4, 0, 0, 0);
            Grid.SetColumn(delete, 2);
            top.Children.Add(delete);
            var guide = Theme.Secondary("결과 별칭이 원본 컬럼이 됩니다 · 변환식·NULL 처리는 컬럼 매핑에서 · Ctrl+Enter 검증");
            guide.HorizontalAlignment = HorizontalAlignment.Right;
            guide.VerticalAlignment = VerticalAlignment.Center;
            guide.TextTrimming = TextTrimming.CharacterEllipsis;
            guide.FontSize = 11.5;
            guide.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(guide, 3);
            top.Children.Add(guide);
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            _nameBox = new TextBox { Width = 170, FontFamily = Theme.Mono };
            _nameBox.LostFocus += (s, e) => ApplyName();
            _sqlBox = new TextBox
            {
                FontFamily = Theme.Mono,
                FontSize = 12,
                AcceptsReturn = true,
                AcceptsTab = false,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                TextWrapping = TextWrapping.NoWrap
            };
            _sqlBox.TextChanged += (s, e) => OnSqlEdited();
            _sqlBox.SelectionChanged += (s, e) => UpdateCaret();
            _sqlBox.PreviewKeyDown += OnEditorKeyDown;
            _lineNumbers = new StackPanel { Margin = new Thickness(0, 4, 8, 4) };
            _banner = Theme.Secondary("");
            _banner.TextWrapping = TextWrapping.Wrap;
            _banner.Margin = new Thickness(0, 6, 0, 6);
            _caret = Theme.Secondary("줄 1, 열 1");
            _resultPanel = new StackPanel();
            _workspace = new Border { Child = BuildWorkspace() };
            root.Children.Add(_workspace);
            Content = root;

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && !_sqlBox.IsFocused)
                {
                    Close();
                }
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    _ = RunValidateAsync();
                    e.Handled = true;
                }
            };

            RefreshPicker();
            if (_host.State.SqlMappings().Count == 0)
            {
                _workspace.Child = BuildEmpty();
            }
        }

        public void LoadMapping(string mappingId)
        {
            _mapping = _host.State.SqlMappings().FirstOrDefault(m => m.Id == mappingId)
                       ?? _host.State.SqlMappings().FirstOrDefault();
            if (_mapping == null)
            {
                _workspace.Child = BuildEmpty();
                return;
            }

            _loading = true;
            Title = "SQL 원본 편집기 — " + _mapping.Source;
            _nameBox.Text = _mapping.Source;
            _sqlBox.Text = _mapping.Sql ?? "";
            _mappingPicker.SelectedValue = _mapping.Id;
            _loading = false;
            RebuildRightPanel();
            RefreshLineNumbers();
            RefreshResult();
        }

        public void PrepareDevHostShot(string tab, bool errorLine)
        {
            if (_mapping == null)
            {
                return;
            }

            _tab = string.IsNullOrEmpty(tab) ? "check" : tab;
            if (errorLine)
            {
                _loading = true;
                _sqlBox.Text = "SELECT\n    BAD_COL\nFROM SRC_CUSTOMER\nWHERE CUSTOMER_ID > :LAST_ID";
                _mapping.Sql = _sqlBox.Text;
                _loading = false;
            }

            var source = _host.State.SourceOf(_mapping);
            var columns = new List<ResultColumn>();
            if (source != null)
            {
                foreach (var column in source.Columns)
                {
                    columns.Add(new ResultColumn { Name = column.Name, Alias = column.Name, Type = column.Type, Expr = column.Name });
                }
            }

            _host.State.SqlCheck[_mapping.Id] = new SqlCheckSession
            {
                At = DateTime.Now,
                Sql = _mapping.Sql,
                Result = errorLine
                    ? new SqlValidationResult
                    {
                        Level = CheckLevels.Error,
                        Items = new List<CheckItem> { new CheckItem { Check = "SQL 구문", Level = CheckLevels.Error, Detail = "ORA-00904: \"BAD_COL\": invalid identifier" } },
                        Columns = new List<ResultColumn>(),
                        ErrorLine = 2
                    }
                    : new SqlValidationResult
                    {
                        Level = CheckLevels.Pass,
                        Items = new List<CheckItem>
                        {
                            new CheckItem { Check = "SQL Valid", Level = CheckLevels.Pass, Detail = columns.Count + "개 결과 열" },
                            new CheckItem { Check = "대상 매핑", Level = CheckLevels.Info, Detail = _mapping.Columns.Count + " / " + columns.Count + " columns linked" }
                        },
                        Columns = columns,
                        Mapped = _mapping.Columns.Count,
                        Total = columns.Count
                    }
            };
            _host.State.SqlPreview[_mapping.Id] = MakePreview(columns);
            SetBanner(errorLine ? "SQL 오류 — ORA-00904 · 2번 줄" : "SQL Valid   Result Columns : " + columns.Count + "   ·   Target Mapping : " + _mapping.Columns.Count + " / " + columns.Count);
            RefreshLineNumbers();
            RefreshResult();
        }

        private UIElement BuildWorkspace()
        {
            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(326) });
            columns.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 왼쪽: 편집기(위) ↔ 결과(아래)를 분할선으로 나눈다 — 창 높이를 채우고 비율은 끌어서 조절
            // 편집기 카드 = 머리 40 + 알림·상태 줄 ≈ 100 + 편집기 — 240px은 있어야 편집기가 몇 줄이라도 보인다
            var left = new SplitPane(_host.State.Ui.SqlSplit, r => _host.State.Ui.SqlSplit = r, 240, 100) { Margin = new Thickness(0, 0, 12, 0) };
            left.Top = BuildEditorCard();
            var tabs = Kit.Segmented(new[]
            {
                new Kit.SegmentOption { Value = "check", Label = "검증 결과" },
                new Kit.SegmentOption { Value = "preview", Label = "미리보기" },
                new Kit.SegmentOption { Value = "alias", Label = "Alias 매핑" },
                new Kit.SegmentOption { Value = "generated", Label = "생성 SQL" }
            }, _tab, v => { _tab = v; RefreshResult(); });
            var resultScroll = new ScrollViewer { Content = _resultPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var resultCard = Kit.Card(Icons.Check + "  SQL 결과", tabs, resultScroll, null);
            left.Bottom = resultCard;
            columns.Children.Add(left);

            // 오른쪽(설정·바인드)은 내용이 길어지면 혼자 스크롤
            var right = new ScrollViewer { Content = _rightPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            RebuildRightPanel();
            Grid.SetColumn(right, 1);
            columns.Children.Add(right);
            columns.SizeChanged += (s, e) =>
            {
                var stacked = e.NewSize.Width < 900;
                columns.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(326);
                Grid.SetColumn(right, stacked ? 0 : 1);
                Grid.SetRow(right, stacked ? 1 : 0);
                Grid.SetColumnSpan(right, stacked ? 2 : 1);
                left.Margin = stacked ? new Thickness(0, 0, 0, 10) : new Thickness(0, 0, 12, 0);
                // 좁은 창에서 설정 패널이 아래로 내려오면 높이의 1/3까지만 — 편집기·결과가 남은 높이를 가진다
                right.MaxHeight = stacked ? Math.Max(120, e.NewSize.Height * 0.33) : double.PositiveInfinity;
            };
            return columns;
        }

        /// <summary>설정·바인드 카드는 고른 매핑에 따라 달라지므로 매핑을 불러올 때마다 다시 만든다.</summary>
        private void RebuildRightPanel()
        {
            _rightPanel.Children.Clear();
            _rightPanel.Children.Add(BuildSettingsCard());
            var binds = BuildBindsCard();
            binds.Margin = new Thickness(0, 10, 0, 0);
            _rightPanel.Children.Add(binds);
        }

        /// <summary>검증·미리보기 결과 한 줄. 글 내용으로 수준(오류·경고·정상·안내)을 가려 알림 상자 색을 맞춘다.</summary>
        private void SetBanner(string text)
        {
            _banner.Text = text ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                _bannerHost.Visibility = Visibility.Collapsed;
                return;
            }

            string kind;
            if (text.Contains("오류") || text.Contains("ORA-"))
            {
                kind = "err";
            }
            else if (text.Contains("경고") || text.Contains("바뀌"))
            {
                kind = "warn";
            }
            else if (text.StartsWith("SQL Valid", StringComparison.Ordinal) || text.Contains(" ms"))
            {
                kind = "ok";
            }
            else
            {
                kind = "info";
            }

            System.Windows.Media.Brush tone = kind == "err" ? Theme.Danger : kind == "warn" ? Theme.Warning : kind == "ok" ? Theme.Success : null;
            if (tone != null)
            {
                _banner.Foreground = tone;
                _bannerHost.BorderBrush = tone;
                var tint = tone.CloneCurrentValue();
                tint.Opacity = 0.14;
                _bannerHost.Background = tint;
            }
            else
            {
                _banner.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
                _bannerHost.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
                _bannerHost.SetResourceReference(Border.BackgroundProperty, Theme.SurfaceBackground);
            }

            _bannerHost.Visibility = Visibility.Visible;
        }

        private UIElement BuildEditorCard()
        {
            var tools = new WrapPanel();
            var validate = Kit.PrimaryButton("SQL 검증", Icons.Check);
            validate.Click += async (s, e) => await RunValidateAsync().ConfigureAwait(true);
            var preview = Kit.Button("100행 미리보기", Icons.View);
            preview.Click += async (s, e) => await RunPreviewAsync().ConfigureAwait(true);
            var alias = Kit.GhostButton("Alias 자동 매핑", Icons.Magic);
            alias.Click += (s, e) => AutoMapAliases();
            _nameBox.Height = 28;
            _nameBox.Margin = new Thickness(0, 0, 8, 0);
            tools.Children.Add(_nameBox);
            tools.Children.Add(validate);
            tools.Children.Add(preview);
            tools.Children.Add(alias);

            // 줄 번호 열과 입력 칸은 같은 줄 높이(17)를 쓰고, 입력 칸이 스크롤하면 줄 번호 열도 같이 스크롤한다
            _sqlBox.MinHeight = 40;
            _sqlBox.BorderThickness = new Thickness(0);
            _sqlBox.Padding = new Thickness(6, 4, 6, 4);
            _sqlBox.SetValue(TextBlock.LineHeightProperty, 17.0);
            _sqlBox.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
            _sqlBox.SetResourceReference(Control.BackgroundProperty, Theme.ControlBackground);
            _lineNumbers.Margin = new Thickness(0, 4, 8, 4);
            _gutterScroll = new ScrollViewer
            {
                Content = _lineNumbers,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                IsHitTestVisible = false
            };
            _sqlBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((s, e) => _gutterScroll.ScrollToVerticalOffset(e.VerticalOffset)));
            var gutter = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Child = _gutterScroll };
            gutter.SetResourceReference(Border.BackgroundProperty, Theme.SurfaceBackground);
            gutter.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            var editor = new Grid();
            editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            editor.Children.Add(gutter);
            Grid.SetColumn(_sqlBox, 1);
            editor.Children.Add(_sqlBox);
            var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = editor };
            frame.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            _bannerHost.Child = _banner;
            var status = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var dialect = Theme.Secondary("Oracle 문법 · 끝의 ; 생략 가능");
            dialect.FontSize = 11.5;
            DockPanel.SetDock(dialect, Dock.Right);
            status.Children.Add(dialect);
            status.Children.Add(_caret);
            // 상태 줄·알림은 아래에 붙이고 편집기가 남은 높이를 채운다(분할선으로 높이 조절)
            var body = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(status, Dock.Bottom);
            body.Children.Add(status);
            DockPanel.SetDock(_bannerHost, Dock.Bottom);
            body.Children.Add(_bannerHost);
            body.Children.Add(frame);
            return Kit.Card(Icons.Code + "  SQL 원본", tools, body, null);
        }

        private Border BuildSettingsCard()
        {
            var body = new StackPanel();
            var targets = new List<Kit.ComboOption>();
            var targetMeta = _host.State.TargetMeta();
            if (targetMeta != null)
            {
                targets.AddRange(targetMeta.Tables.Where(t => t.Kind == "TABLE").Select(t => new Kit.ComboOption { Value = t.Name, Label = t.Name }));
            }
            body.Children.Add(Kit.Field("대상 테이블", Kit.CellComboBox(targets, _mapping != null ? _mapping.Target : "", v => { if (_mapping != null) { _mapping.Target = v; _host.State.MarkChanged(); } }), true, "대상 스키마의 쓰기 테이블"));
            var modes = WriteModes.All.Select(x => new Kit.ComboOption { Value = x.Value, Label = x.Label }).ToList();
            body.Children.Add(Kit.Field("쓰기 방식", Kit.CellComboBox(modes, _mapping != null ? _mapping.Mode : WriteModes.Merge, v => { if (_mapping != null) { _mapping.Mode = v; _host.State.MarkChanged(); } }), false, null));
            var keyText = _mapping != null && _mapping.MergeKey.Count > 0 ? string.Join(" · ", _mapping.MergeKey) : "키 없음";
            body.Children.Add(Kit.Field("병합 키", Kit.Tag(keyText, "pk"), false, null));
            var sizes = Kit.FormGrid(2,
                Kit.Field("Fetch 크기", Kit.CellTextBox(_mapping != null && _mapping.FetchSize != null ? _mapping.FetchSize.Value.ToString("N0") : "", v => SetSize(true, v), true), false, "5,000 (작업 기본값)"),
                Kit.Field("커밋 크기", Kit.CellTextBox(_mapping != null && _mapping.CommitSize != null ? _mapping.CommitSize.Value.ToString("N0") : "", v => SetSize(false, v), true), false, "5,000 (작업 기본값)"));
            body.Children.Add(sizes);
            var checkpoint = new List<Kit.ComboOption> { new Kit.ComboOption { Value = "", Label = "(없음 — 재개 불가)" } };
            var source = _mapping != null ? _host.State.SourceOf(_mapping) : null;
            if (source != null)
            {
                checkpoint.AddRange(source.Columns.Select(c => new Kit.ComboOption { Value = c.Name, Label = c.Name }));
            }
            body.Children.Add(Kit.Field("체크포인트 컬럼", Kit.CellComboBox(checkpoint, _mapping != null ? _mapping.CheckpointColumn : "", v => { if (_mapping != null) { _mapping.CheckpointColumn = string.IsNullOrEmpty(v) ? null : v; _host.State.MarkChanged(); } }), false, "결과 열을 순서대로 읽고 마지막 값을 남깁니다"));
            var use = new CheckBox { Content = "이번 작업에서 사용", IsChecked = _mapping == null || _mapping.Use };
            use.Click += (s, e) => { if (_mapping != null) { _mapping.Use = use.IsChecked == true; _host.State.MarkChanged(); } };
            body.Children.Add(use);
            foreach (UIElement child in body.Children)
            {
                var fe = child as FrameworkElement;
                if (fe != null && fe.Margin.Bottom == 0)
                {
                    fe.Margin = new Thickness(0, 0, 0, 10);
                }
            }

            return Kit.Card(Icons.Setting + "  SQL 원본 설정", null, body, null);
        }

        private Border BuildBindsCard()
        {
            var body = new StackPanel();
            if (_mapping == null || _mapping.Binds == null || _mapping.Binds.Count == 0)
            {
                body.Children.Add(Theme.Secondary("SQL에 :이름 형태의 바인드 변수가 없습니다."));
            }
            else
            {
                foreach (var bind in _mapping.Binds)
                {
                    var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                    row.Children.Add(Kit.BindNameText(":" + bind.Name));
                    var types = new[] { "NUMBER", "VARCHAR2", "DATE" }.Select(x => new Kit.ComboOption { Value = x, Label = x }).ToList();
                    var type = Kit.CellComboBox(types, bind.Type, v => { bind.Type = v; _host.State.MarkChanged(); });
                    Grid.SetColumn(type, 1);
                    row.Children.Add(type);
                    var value = Kit.CellTextBox(bind.Value, v => { bind.Value = v; _host.State.MarkChanged(); }, true);
                    Grid.SetColumn(value, 2);
                    row.Children.Add(value);
                    var cp = new CheckBox { IsChecked = bind.FromCheckpoint, ToolTip = "체크포인트" };
                    cp.Click += (s, e) => { bind.FromCheckpoint = cp.IsChecked == true; _host.State.MarkChanged(); };
                    Grid.SetColumn(cp, 3);
                    row.Children.Add(cp);
                    body.Children.Add(row);
                }
            }
            return Kit.Card(":  바인드 변수 (Bind Parameters)", null, body, null);
        }

        private UIElement BuildEmpty()
        {
            var empty = Kit.EmptyState(Icons.Code, "SQL 원본 매핑이 없습니다", "테이블 매핑에서 SQL 원본을 추가하거나 예제 SQL로 시작하세요.");
            var add = Kit.PrimaryButton("SQL 원본 추가", Icons.Add);
            add.Click += (s, e) => _host.OpenAddMapping(null, true);
            empty.Children.Add(add);
            return empty;
        }

        private void RefreshPicker()
        {
            _mappingPicker.Items.Clear();
            foreach (var mapping in _host.State.SqlMappings())
            {
                _mappingPicker.Items.Add(new Kit.ComboOption { Value = mapping.Id, Label = mapping.Source + " → " + mapping.Target + (mapping.Use ? "" : " (사용 안 함)") });
            }
            _mappingPicker.DisplayMemberPath = "Label";
            _mappingPicker.SelectedValuePath = "Value";
            if (_mapping != null)
            {
                _mappingPicker.SelectedValue = _mapping.Id;
            }
        }

        private void OnSqlEdited()
        {
            if (_loading || _mapping == null)
            {
                return;
            }
            _mapping.Sql = _sqlBox.Text;
            SqlSourceBinds.Sync(_mapping);
            _host.State.MarkChanged();
            _banner.Text = SqlEditorLogic.SqlChangedBanner(_host.State.SqlCheck.ContainsKey(_mapping.Id), _sqlBox.Text,
                _host.State.SqlCheck.TryGetValue(_mapping.Id, out var check) ? check.Sql : null) ?? "";
            RefreshLineNumbers();
        }

        private async Task RunValidateAsync()
        {
            if (_mapping == null)
            {
                return;
            }
            CancelRequest();
            var ct = _requestCts.Token;
            try
            {
                SetBanner("검증 중…");
                var result = await _host.Operations.ValidateSqlMappingAsync(_mapping, ct).ConfigureAwait(true);
                if (result == null || ct.IsCancellationRequested)
                {
                    return;
                }
                _host.State.SqlCheck[_mapping.Id] = new SqlCheckSession { Result = result, At = DateTime.Now, Sql = _mapping.Sql };
                SetBanner(result.Level == CheckLevels.Error ? "SQL 오류가 있습니다" : "SQL Valid · 결과 열 " + result.Columns.Count + "개");
                _tab = "check";
                RefreshLineNumbers();
                RefreshResult();
                _host.State.MarkChanged();
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task RunPreviewAsync()
        {
            if (_mapping == null)
            {
                return;
            }
            CancelRequest();
            var ct = _requestCts.Token;
            try
            {
                SetBanner("읽는 중…");
                var result = await _host.Operations.PreviewSqlMappingAsync(_mapping, 100, ct).ConfigureAwait(true);
                if (result == null || ct.IsCancellationRequested)
                {
                    return;
                }
                _host.State.SqlPreview[_mapping.Id] = new SqlPreviewSession { Result = result, At = DateTime.Now, Sql = _mapping.Sql };
                SetBanner(result.Rows.Count + "행 · " + result.ElapsedMs + " ms");
                _tab = "preview";
                RefreshResult();
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void RefreshResult()
        {
            _resultPanel.Children.Clear();
            if (_mapping == null)
            {
                return;
            }
            if (_tab == "preview")
            {
                BuildPreviewResult();
            }
            else if (_tab == "alias")
            {
                BuildAliasResult();
            }
            else if (_tab == "generated")
            {
                BuildGeneratedResult();
            }
            else
            {
                BuildCheckResult();
            }
        }

        private void BuildCheckResult()
        {
            if (!_host.State.SqlCheck.TryGetValue(_mapping.Id, out var session) || session.Result == null)
            {
                _resultPanel.Children.Add(Kit.EmptyState(Icons.Check, "아직 검증하지 않았습니다", "Ctrl+Enter로 SQL을 검증하세요."));
                return;
            }
            foreach (var item in session.Result.Items)
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var pill = Kit.Pill(item.Level == CheckLevels.Error ? "err" : item.Level == CheckLevels.Warn ? "warn" : "ok", item.Level, null);
                DockPanel.SetDock(pill, Dock.Left);
                row.Children.Add(pill);
                var text = Theme.Text(item.Check + " · " + item.Detail);
                text.TextWrapping = TextWrapping.Wrap;
                text.Margin = new Thickness(8, 2, 0, 0);
                row.Children.Add(text);
                _resultPanel.Children.Add(row);
            }
        }

        private void BuildPreviewResult()
        {
            if (!_host.State.SqlPreview.TryGetValue(_mapping.Id, out var session) || session.Result == null)
            {
                _resultPanel.Children.Add(Kit.EmptyState(Icons.View, "미리보기가 없습니다", "100행 미리보기를 실행하세요."));
                return;
            }
            var result = session.Result;
            var columns = new List<RowGridColumn> { new RowGridColumn { Header = "#", Width = new GridLength(34) } };
            foreach (var column in result.Columns)
            {
                columns.Add(new RowGridColumn { Header = column.Name, Width = new GridLength(130) });
            }
            var table = new RowGrid(columns.ToArray());
            for (var i = 0; i < result.Rows.Count; i++)
            {
                var cells = new List<FrameworkElement> { Theme.Secondary((i + 1).ToString()) };
                cells.AddRange(result.Rows[i].Select(value =>
                {
                    var text = Theme.Text(value ?? "NULL");
                    text.FontFamily = Theme.Mono;
                    text.FontSize = 12;
                    text.TextTrimming = TextTrimming.CharacterEllipsis;
                    if (value == null)
                    {
                        text.Opacity = 0.55;
                        text.FontStyle = FontStyles.Italic;
                    }
                    return (FrameworkElement)text;
                }));
                table.AddRow(cells.ToArray(), i, null, null);
            }
            var scroll = new ScrollViewer { Content = table.Root, Tag = "AllowHScroll", HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 260 };
            _resultPanel.Children.Add(Theme.Secondary(result.Rows.Count + "행 · " + result.ElapsedMs + " ms"));
            _resultPanel.Children.Add(scroll);
        }

        private void BuildAliasResult()
        {
            if (!_host.State.SqlCheck.TryGetValue(_mapping.Id, out var session) || session.Result == null)
            {
                _resultPanel.Children.Add(Kit.EmptyState(Icons.List, "Alias 결과가 없습니다", "먼저 SQL을 검증하세요."));
                return;
            }
            var target = _host.State.TargetTable(_mapping.Target);
            var rows = SqlEditorLogic.AliasRows(_mapping, session.Result, target);
            var grid = new RowGrid(
                new RowGridColumn { Header = "SQL 결과 열", Width = new GridLength(1, GridUnitType.Star) },
                new RowGridColumn { Header = "SELECT 식", Width = new GridLength(1.2, GridUnitType.Star) },
                new RowGridColumn { Header = "추정 형식", Width = new GridLength(100) },
                new RowGridColumn { Header = "→ 대상 컬럼", Width = new GridLength(1, GridUnitType.Star) },
                new RowGridColumn { Header = "검사", Width = new GridLength(64) });
            foreach (var row in rows)
            {
                grid.AddRow(new FrameworkElement[]
                {
                    Theme.Text(row.ResultColumn), Theme.Secondary(row.SelectExpr), Theme.Secondary(row.TypeHint), Theme.Text(row.TargetColumn),
                    Kit.Pill(row.CheckLevel == CheckLevels.Error ? "err" : row.CheckLevel == CheckLevels.Warn ? "warn" : "ok", row.CheckLevel, null)
                }, row, null, null);
            }
            _resultPanel.Children.Add(grid.Root);
            var missing = SqlEditorLogic.MissingTargetColumnsSummary(_mapping, target);
            if (!string.IsNullOrEmpty(missing))
            {
                var note = Theme.Secondary("값이 없는 대상 컬럼: " + missing);
                note.Margin = new Thickness(0, 6, 0, 0);
                note.TextWrapping = TextWrapping.Wrap;
                _resultPanel.Children.Add(note);
            }
        }

        private void BuildGeneratedResult()
        {
            var source = _host.State.SourceOf(_mapping);
            var target = _host.State.TargetTable(_mapping.Target);
            var sourceSchema = _host.State.Job.Source != null ? _host.State.Job.Source.Schema : "";
            var targetSchema = _host.State.Job.Target != null ? _host.State.Job.Target.Schema : "";
            var strategy = _host.State.Job.Strategy;
            var select = SqlGenerator.BuildSourceSelect(_mapping, sourceSchema, source, target, new SourceSelectOptions { FetchSize = _mapping.FetchSize ?? strategy.FetchSize, Workers = strategy.Workers });
            var write = SqlGenerator.BuildWriteSql(targetSchema, _mapping.Target, _mapping.Columns.Select(c => c.Target).ToList(), _mapping.Mode, _mapping.MergeKey, strategy.ErrorTable);
            _resultPanel.Children.Add(Kit.SectionLabel("원본 SELECT"));
            _resultPanel.Children.Add(SqlBox(select));
            _resultPanel.Children.Add(Kit.SectionLabel("대상 쓰기 문"));
            _resultPanel.Children.Add(SqlBox(write));
        }

        private static TextBox SqlBox(string sql)
        {
            return new TextBox { Text = sql, IsReadOnly = true, FontFamily = Theme.Mono, FontSize = 12, TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 150 };
        }

        private void AutoMapAliases()
        {
            if (_mapping == null || !_host.State.SqlCheck.TryGetValue(_mapping.Id, out var session) || session.Result == null)
            {
                return;
            }
            var target = _host.State.TargetTable(_mapping.Target);
            if (target == null)
            {
                return;
            }
            var source = session.Result.Columns.Select(c => new ColumnMetadata { Name = string.IsNullOrEmpty(c.Alias) ? c.Name : c.Alias, Type = c.Type }).ToList();
            foreach (var item in MappingService.AutoMapColumns(source, target.Columns))
            {
                var current = _mapping.FindColumn(item.Mapping.Target);
                if (current != null)
                {
                    current.Source = item.Mapping.Source;
                    current.Expr = item.Mapping.Expr;
                }
            }
            _host.State.MarkChanged();
            _tab = "alias";
            RefreshResult();
        }

        private void RefreshLineNumbers()
        {
            _lineNumbers.Children.Clear();
            SqlValidationResult validation = null;
            if (_mapping != null && _host.State.SqlCheck.TryGetValue(_mapping.Id, out var session))
            {
                validation = session.Result;
            }
            foreach (var line in SqlEditorLogic.LineStyles(_sqlBox.Text, validation))
            {
                var text = Theme.Secondary(line.Line.ToString());
                text.FontFamily = Theme.Mono;
                text.FontSize = 12;
                text.TextAlignment = TextAlignment.Right;
                text.Height = 17;
                text.LineHeight = 17;
                text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                if (line.IsError)
                {
                    text.Foreground = Theme.Danger;
                    text.Background = Theme.DangerTint;
                    text.FontWeight = FontWeights.Bold;
                }
                _lineNumbers.Children.Add(text);
            }
        }

        private void UpdateCaret()
        {
            var line = Math.Max(0, _sqlBox.GetLineIndexFromCharacterIndex(_sqlBox.CaretIndex));
            var start = line >= 0 ? _sqlBox.GetCharacterIndexFromLineIndex(line) : 0;
            _caret.Text = "줄 " + (line + 1) + ", 열 " + (_sqlBox.CaretIndex - start + 1);
        }

        private void OnEditorKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Tab)
            {
                _sqlBox.SelectedText = "    ";
                e.Handled = true;
            }
        }

        private void ApplyName()
        {
            if (_mapping == null)
            {
                return;
            }
            var normalized = new string((_nameBox.Text ?? "").ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '#' ? c : '_').ToArray());
            _mapping.Source = normalized;
            _nameBox.Text = normalized;
            Title = "SQL 원본 편집기 — " + normalized;
            _host.State.MarkChanged();
            RefreshPicker();
        }

        private void SetSize(bool fetch, string text)
        {
            if (_mapping == null)
            {
                return;
            }
            var normalized = (text ?? "").Replace(",", "");
            int value;
            int? parsed = int.TryParse(normalized, out value) && value > 0 ? value : (int?)null;
            if (fetch)
            {
                _mapping.FetchSize = parsed;
            }
            else
            {
                _mapping.CommitSize = parsed;
            }
        }

        private void CancelRequest()
        {
            if (_requestCts != null)
            {
                _requestCts.Cancel();
                _requestCts.Dispose();
            }
            _requestCts = new CancellationTokenSource();
        }

        private static SqlPreviewSession MakePreview(IList<ResultColumn> columns)
        {
            var result = new QueryResult { Columns = new List<QueryColumn>(), Rows = new List<string[]>(), ElapsedMs = 18 };
            foreach (var column in columns)
            {
                result.Columns.Add(new QueryColumn { Name = string.IsNullOrEmpty(column.Alias) ? column.Name : column.Alias, Type = column.Type });
            }
            for (var row = 0; row < 6; row++)
            {
                result.Rows.Add(result.Columns.Select((c, i) => i == 2 && row == 4 ? null : c.Name + "_" + (row + 1)).ToArray());
            }
            return new SqlPreviewSession { Result = result, At = DateTime.Now };
        }
    }
}
