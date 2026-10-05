using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using MigrationStudio.Logic;
using MigrationStudio.Services;

namespace MigrationStudio.Ui.Settings
{
    internal sealed class MigrationSettingsView : UserControl
    {
        private static readonly Tuple<string, string, string>[] CheckpointStores =
        {
            Tuple.Create("AUTO", "자동 (권장)", "대상에 MIG_CHECKPOINT를 만들 권한이 있으면 대상 DB, 없으면 로컬 파일"),
            Tuple.Create("TARGET", "대상 DB 제어 테이블", "데이터 배치와 같은 트랜잭션으로 저장 — 커밋과 체크포인트가 절대 어긋나지 않음"),
            Tuple.Create("LOCAL", "로컬 파일", "대상 DB를 건드리지 않음 — 커밋 직후 끊기면 마지막 배치를 한 번 더 처리")
        };

        private MigrationSettings _working;
        private string _revision;
        private readonly MigrationJob _jobContext;
        private readonly StackPanel _tabBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        private readonly StackPanel _content = new StackPanel();
        private readonly TextBlock _errorBanner = new TextBlock { Foreground = Theme.Danger, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
        private string _selectedTab = "connections";
        private ListBox _profileList;
        private StackPanel _profileEditor;
        private readonly Dictionary<string, Button> _tabButtons = new Dictionary<string, Button>();
        private readonly Dictionary<string, Border> _tabUnderlines = new Dictionary<string, Border>();

        public MigrationSettingsView(MigrationJob jobContext)
        {
            _jobContext = jobContext;
            var root = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var inner = new StackPanel { Margin = new Thickness(12) };
            var hint = Theme.Secondary("Migration Studio 플러그인의 설정입니다. Folderss 설정 > 플러그인 > Migration Studio에서도 같은 내용을 고칩니다.");
            hint.FontSize = 12;
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 0, 0, 8);
            inner.Children.Add(hint);
            inner.Children.Add(_tabBar);
            inner.Children.Add(_errorBanner);
            inner.Children.Add(_content);
            root.Content = inner;
            Content = root;
        }

        public void LoadFromRepository()
        {
            var loaded = SettingsRepository.Load(AppServices.Manager);
            _working = CloneSettings(loaded.Settings);
            _revision = loaded.Revision;
            RebuildTabs();
            RenderTab();
        }

        public bool Commit()
        {
            string err;
            var failedId = SettingsLogic.ValidateAll(_working, out err);
            if (failedId != null)
            {
                _selectedTab = "connections";
                RenderTab();
                SelectProfile(failedId);
                _errorBanner.Text = err;
                _errorBanner.Visibility = Visibility.Visible;
                return false;
            }

            _errorBanner.Visibility = Visibility.Collapsed;
            SettingsRepository.Save(AppServices.Manager, _working, _revision);
            _revision = MigrationSettingsStore.Serialize(_working);
            return true;
        }

        public void SelectTab(string tab)
        {
            _selectedTab = tab ?? "connections";
            RebuildTabs();
            RenderTab();
        }

        private void RebuildTabs()
        {
            _tabBar.Children.Clear();
            _tabButtons.Clear();
            _tabUnderlines.Clear();
            var count = _working != null ? _working.Connections.Count : 0;
            AddTab("접속 " + count.ToString(CultureInfo.InvariantCulture), "connections");
            AddTab("기본값", "defaults");
            AddTab("실행 에이전트", "agent");
        }

        private void AddTab(string label, string key)
        {
            Border underline;
            var text = Theme.Text(label);
            text.FontSize = 13;
            var btn = WorkspaceUi.SelectorButton(text, out underline);
            btn.Click += (s, e) => { _selectedTab = key; RebuildTabs(); RenderTab(); };
            _tabButtons[key] = btn;
            _tabUnderlines[key] = underline;
            WorkspaceUi.SetSelected(btn, underline, string.Equals(_selectedTab, key, StringComparison.Ordinal));
            _tabBar.Children.Add(btn);
        }

        private void RenderTab()
        {
            _content.Children.Clear();
            if (_working == null)
            {
                LoadFromRepository();
                return;
            }

            foreach (var pair in _tabButtons)
            {
                WorkspaceUi.SetSelected(pair.Value, _tabUnderlines[pair.Key], string.Equals(_selectedTab, pair.Key, StringComparison.Ordinal));
            }

            if (_selectedTab == "connections")
            {
                RenderConnections();
            }
            else if (_selectedTab == "defaults")
            {
                RenderDefaults();
            }
            else
            {
                RenderAgent();
            }
        }

        private void RenderConnections()
        {
            _profileList = new ListBox { MinHeight = 280, BorderThickness = new Thickness(0) };
            ScrollViewer.SetHorizontalScrollBarVisibility(_profileList, ScrollBarVisibility.Disabled);
            _profileList.SelectionChanged += (s, e) => RenderSelectedProfile();
            RebuildProfileList();
            var tools = new StackPanel { Orientation = Orientation.Horizontal };
            var add = Kit.IconButton(Icons.Add, "추가");
            add.Click += (s, e) =>
            {
                var p = SettingsLogic.AddProfile(_working);
                RebuildProfileList();
                RebuildTabs();
                SelectProfile(p.Id);
            };
            var dup = Kit.IconButton(Icons.Copy, "복제");
            dup.Click += (s, e) =>
            {
                var idx = _profileList.SelectedIndex;
                if (idx < 0 || idx >= _working.Connections.Count)
                {
                    return;
                }

                var copy = SettingsLogic.DuplicateProfile(_working, _working.Connections[idx]);
                RebuildProfileList();
                RebuildTabs();
                SelectProfile(copy.Id);
            };
            var del = Kit.IconButton(Icons.Del, "삭제");
            del.Click += (s, e) =>
            {
                var idx = _profileList.SelectedIndex;
                if (idx < 0 || idx >= _working.Connections.Count)
                {
                    return;
                }

                var p = _working.Connections[idx];
                if (!SettingsLogic.CanDeleteProfile(_jobContext, p))
                {
                    Kit.Toast(Window.GetWindow(this), "지금 작업의 원본·대상 접속이라 지울 수 없습니다", "warn");
                    return;
                }

                _working.Connections.RemoveAt(idx);
                RebuildProfileList();
                RebuildTabs();
                RenderTab();
            };
            tools.Children.Add(add);
            tools.Children.Add(dup);
            tools.Children.Add(del);
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var left = Kit.Card("", tools, _profileList, null);
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);
            var editorScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            _profileEditor = new StackPanel();
            editorScroll.Content = _profileEditor;
            Grid.SetColumn(editorScroll, 1);
            grid.Children.Add(editorScroll);
            _content.Children.Add(grid);
            if (_working.Connections.Count == 0)
            {
                _profileEditor.Children.Add(Kit.EmptyState(Icons.Link, "접속이 없습니다", "[추가]로 원본·대상 접속을 만드세요."));
            }
            else if (_profileList.SelectedIndex < 0)
            {
                _profileList.SelectedIndex = 0;
            }
        }

        private void RebuildProfileList()
        {
            if (_profileList == null)
            {
                return;
            }

            var sel = _profileList.SelectedIndex;
            _profileList.Items.Clear();
            foreach (var p in _working.Connections)
            {
                var row = new Grid { Margin = new Thickness(4, 6, 4, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                var fill = Theme.ConnectionColor(p.Color);
                if (fill != null)
                {
                    dot.Background = fill;
                }
                else
                {
                    dot.SetResourceReference(Border.BackgroundProperty, Theme.DisabledText);
                }

                // 가로 StackPanel은 칸 폭에 맞춰 줄여 주지 않아 말줄임이 안 먹고 오른쪽 꼬리표와 겹친다 — 점 | 글자(1*) Grid로
                var left = new Grid();
                left.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                left.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                left.Children.Add(dot);
                var texts = new StackPanel();
                Grid.SetColumn(texts, 1);
                var name = Theme.Text(p.Name ?? "");
                name.FontFamily = Theme.Mono;
                name.FontSize = 12;
                name.FontWeight = FontWeights.SemiBold;
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                texts.Children.Add(name);
                var addr = Theme.Secondary(ConnectionLogic.Address(p));
                addr.FontSize = 11;
                addr.TextTrimming = TextTrimming.CharacterEllipsis;
                texts.Children.Add(addr);
                left.Children.Add(texts);
                Grid.SetColumn(left, 0);
                row.Children.Add(left);
                var roles = SettingsLogic.RoleLabelForProfile(_jobContext, p.Id);
                if (!string.IsNullOrEmpty(roles) || p.WriteBlocked)
                {
                    var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                    if (!string.IsNullOrEmpty(roles))
                    {
                        var rl = Theme.Secondary(roles);
                        rl.FontSize = 11;
                        rl.Margin = new Thickness(8, 0, 0, 0);
                        right.Children.Add(rl);
                    }

                    if (p.WriteBlocked)
                    {
                        var tag = Kit.Tag("쓰기 금지", "ro");
                        tag.Margin = new Thickness(4, 0, 0, 0);
                        right.Children.Add(tag);
                    }

                    Grid.SetColumn(right, 1);
                    row.Children.Add(right);
                }

                _profileList.Items.Add(row);
            }

            if (sel >= 0 && sel < _profileList.Items.Count)
            {
                _profileList.SelectedIndex = sel;
            }
        }

        private void SelectProfile(string id)
        {
            if (_profileList == null)
            {
                return;
            }

            for (var i = 0; i < _working.Connections.Count; i++)
            {
                if (string.Equals(_working.Connections[i].Id, id, StringComparison.Ordinal))
                {
                    _profileList.SelectedIndex = i;
                    break;
                }
            }
        }

        private void RenderSelectedProfile()
        {
            if (_profileEditor == null || _profileList == null)
            {
                return;
            }

            _profileEditor.Children.Clear();
            var idx = _profileList.SelectedIndex;
            if (idx < 0 || idx >= _working.Connections.Count)
            {
                return;
            }

            var p = _working.Connections[idx];
            var name = new TextBox { Text = p.Name ?? "", FontFamily = Theme.Mono };
            name.TextChanged += (s, e) => { p.Name = name.Text; RebuildProfileList(); };
            var color = new ComboBox();
            foreach (var opt in new[] { Tuple.Create("", "없음"), Tuple.Create("green", "초록 — 개발"), Tuple.Create("yellow", "노랑 — 검증"), Tuple.Create("red", "빨강 — 운영") })
            {
                color.Items.Add(new ComboBoxItem { Content = opt.Item2, Tag = opt.Item1 });
                if (string.Equals(p.Color ?? "", opt.Item1, StringComparison.Ordinal))
                {
                    color.SelectedIndex = color.Items.Count - 1;
                }
            }

            color.SelectionChanged += (s, e) =>
            {
                var item = color.SelectedItem as ComboBoxItem;
                p.Color = item != null ? item.Tag as string : "";
                RebuildProfileList();
            };
            var kind = new ComboBox();
            foreach (var k in DatabaseAdapters.Kinds)
            {
                kind.Items.Add(new ComboBoxItem { Content = k.Label, Tag = k.Value, IsEnabled = !k.Planned });
                if (string.Equals(p.Kind ?? "oracle", k.Value, StringComparison.OrdinalIgnoreCase))
                {
                    kind.SelectedIndex = kind.Items.Count - 1;
                }
            }

            kind.SelectionChanged += (s, e) =>
            {
                var item = kind.SelectedItem as ComboBoxItem;
                if (item != null && item.Tag is string v)
                {
                    p.Kind = v;
                }
            };
            var schema = new TextBox { Text = p.DefaultSchema ?? "", FontFamily = Theme.Mono };
            schema.TextChanged += (s, e) => p.DefaultSchema = schema.Text.ToUpperInvariant();
            var hostBox = new TextBox { Text = p.Host ?? "" };
            hostBox.TextChanged += (s, e) => p.Host = hostBox.Text;
            var portBox = new TextBox { Text = p.Port.ToString(CultureInfo.InvariantCulture) };
            portBox.TextChanged += (s, e) =>
            {
                int v;
                if (int.TryParse(portBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                {
                    p.Port = v;
                }
            };
            var service = new TextBox { Text = p.Service ?? "" };
            service.TextChanged += (s, e) => p.Service = service.Text;
            var user = new TextBox { Text = p.User ?? "" };
            user.TextChanged += (s, e) => p.User = user.Text;
            var pass = DialogKit.PasswordInput();
            if (p.SavePassword && !string.IsNullOrEmpty(p.ProtectedPassword))
            {
                pass.Password = "";
            }

            pass.PasswordChanged += (s, e) => p.LegacyPassword = pass.Password;
            var savePass = new CheckBox { Content = "저장", IsChecked = p.SavePassword, Margin = new Thickness(0, 4, 0, 0) };
            savePass.ToolTip = "DPAPI로 암호화해 이 PC의 이 Windows 사용자만 풀 수 있게 저장";
            savePass.Checked += (s, e) => p.SavePassword = true;
            savePass.Unchecked += (s, e) => p.SavePassword = false;
            var writeBlock = new CheckBox { Content = "쓰기 금지 (원본 전용)", IsChecked = p.WriteBlocked };
            writeBlock.Checked += (s, e) => p.WriteBlocked = true;
            writeBlock.Unchecked += (s, e) => p.WriteBlocked = false;
            var passField = Kit.Field("비밀번호", pass, false, null);
            var passRow = new StackPanel();
            passRow.Children.Add(passField);
            passRow.Children.Add(savePass);
            _profileEditor.Children.Add(Kit.FormGrid(2,
                Kit.Field("접속 이름", name, true, null),
                Kit.Field("색 표시", color, false, null),
                Kit.Field("DB 종류", kind, false, null),
                Kit.Field("기본 스키마", schema, false, "비우면 사용자 이름"),
                Kit.Field("호스트", hostBox, true, null),
                Kit.Field("포트", portBox, true, null),
                Kit.Field("서비스명", service, true, "EZConnect: 호스트:포트/서비스명"),
                Kit.Field("사용자", user, true, null),
                passRow,
                Kit.Field("안전", writeBlock, false, "운영 원본 DB에 실수로 쓰지 않게")));
            var errors = MigrationSettingsStore.ValidateProfile(p, _working.Connections);
            if (errors.Count > 0)
            {
                _profileEditor.Children.Add(Kit.Notice("err", new List<Inline> { new Run(string.Join(" · ", errors)) }));
            }

            var testRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var test = Kit.Button("접속 테스트", Icons.Link);
            test.HorizontalAlignment = HorizontalAlignment.Left;
            test.Click += async (s, e) =>
            {
                var pwd = string.IsNullOrEmpty(p.LegacyPassword) ? ConnectionServiceStubPassword(p) : p.LegacyPassword;
                try
                {
                    var target = ConnectionTarget.From(p, pwd ?? "");
                    var adapter = AppServices.DatabaseAdapter ?? DatabaseAdapters.For(p.Kind);
                    var result = await adapter.TestAsync(target, p.WriteBlocked, default).ConfigureAwait(true);
                    testRow.Children.Clear();
                    testRow.Children.Add(test);
                    if (result.Ok)
                    {
                        testRow.Children.Add(Kit.Pill("ok", "✓ Connected", Icons.Check));
                        var detail = " " + (result.Version ?? "");
                        if (result.LatencyMs != null && result.LatencyMs.Value > 0)
                        {
                            detail += " · " + result.LatencyMs.Value.ToString(CultureInfo.InvariantCulture) + " ms";
                        }

                        testRow.Children.Add(Theme.Text(detail));
                    }
                    else
                    {
                        testRow.Children.Add(Kit.Notice("err", new List<Inline> { new Run(result.Error ?? "연결 실패") }));
                    }
                }
                catch (Exception ex)
                {
                    testRow.Children.Add(Kit.Notice("err", new List<Inline> { new Run(ex.Message) }));
                }
            };
            testRow.Children.Add(test);
            _profileEditor.Children.Add(testRow);
        }

        private static string ConnectionServiceStubPassword(ConnectionProfile p)
        {
            if (!string.IsNullOrEmpty(p.ProtectedPassword) && p.SavePassword)
            {
                string plain;
                if (PasswordProtector.TryUnprotect(p.ProtectedPassword, out plain))
                {
                    return plain;
                }
            }

            return "";
        }

        private void RenderDefaults()
        {
            var d = _working.Defaults ?? new MigrationDefaults();
            _content.Children.Add(Kit.SectionLabel("새 작업의 이관 전략 기본값"));
            var commit = CommitSelect(d.CommitSize, v => d.CommitSize = v);
            var fetch = FetchSelect(d.FetchSize, v => d.FetchSize = v);
            var workers = Kit.Segmented(new List<Kit.SegmentOption>
            {
                Seg(1), Seg(2), Seg(4), Seg(8)
            }, d.Workers.ToString(CultureInfo.InvariantCulture), v =>
            {
                int w;
                if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out w))
                {
                    d.Workers = w;
                }
            });
            var errPolicy = new ComboBox();
            foreach (var opt in new[] { ErrorPolicies.Continue, ErrorPolicies.Stop, ErrorPolicies.Retry })
            {
                errPolicy.Items.Add(ErrorPolicies.Label(opt));
                if (string.Equals(d.ErrorPolicy, opt, StringComparison.Ordinal))
                {
                    errPolicy.SelectedIndex = errPolicy.Items.Count - 1;
                }
            }

            errPolicy.SelectionChanged += (s, e) =>
            {
                var idx = errPolicy.SelectedIndex;
                d.ErrorPolicy = idx == 1 ? ErrorPolicies.Stop : idx == 2 ? ErrorPolicies.Retry : ErrorPolicies.Continue;
            };
            var errTable = new TextBox { Text = d.ErrorTable ?? "", FontFamily = Theme.Mono };
            errTable.TextChanged += (s, e) => d.ErrorTable = errTable.Text.ToUpperInvariant();
            _content.Children.Add(Kit.FormGrid(3,
                Kit.Field("커밋 단위", commit, false, null),
                Kit.Field("Fetch 크기", fetch, false, null),
                Kit.Field("병렬 작업자", workers, false, null),
                Kit.Field("오류 처리", errPolicy, false, null),
                Kit.Field("오류 테이블 접두어", errTable, false, "ERR$_ → ERR$_TB_MEMBER")));
            _content.Children.Add(Kit.SectionLabel("체크포인트 저장소"));
            var cpItems = new List<Kit.RadioCardItem>();
            foreach (var store in CheckpointStores)
            {
                cpItems.Add(new Kit.RadioCardItem { Value = store.Item1, Title = store.Item2, Description = store.Item3 });
            }

            _content.Children.Add(Kit.RadioCards("cpstore", cpItems, d.CheckpointStore ?? "AUTO", v => d.CheckpointStore = v));
            var prefix = new TextBox { Text = d.ControlPrefix ?? "", FontFamily = Theme.Mono };
            prefix.TextChanged += (s, e) => d.ControlPrefix = prefix.Text.ToUpperInvariant();
            var cp = d.ControlPrefix ?? "MIG_";
            _content.Children.Add(Kit.Field("제어 테이블 접두어", prefix, false, cp + "RUN · " + cp + "RUN_TASK · " + cp + "CHECKPOINT (대상 스키마)"));
        }

        private void RenderAgent()
        {
            var a = _working.Agent ?? new AgentSettings();
            _content.Children.Add(Kit.Notice("info", new List<Inline>
            {
                new Run("이관은 Folderss 안이 아니라 플러그인이 띄우는 "),
                new Run("MigrationAgent.exe") { FontWeight = FontWeights.SemiBold },
                new Run(" 하위 프로세스에서 실행합니다.")
            }));
            _content.Children.Add(Kit.SectionLabel("Folderss를 닫을 때"));
            var exitItems = new List<Kit.RadioCardItem>
            {
                new Kit.RadioCardItem { Value = "CONTINUE", Title = "계속 실행 (권장)", Description = "에이전트는 그대로 돌고, Folderss를 다시 열어 Migration Studio를 열면 진행 화면에 다시 붙음" },
                new Kit.RadioCardItem { Value = "STOP", Title = "함께 중지", Description = "Folderss가 끝나면 에이전트도 끝냄(Job Object) — 진행 중 배치는 롤백, 체크포인트에서 재개" }
            };
            _content.Children.Add(Kit.RadioCards("hostexit", exitItems, a.OnHostExit ?? "CONTINUE", v => a.OnHostExit = v));
            var concurrent = Kit.Segmented(new List<Kit.SegmentOption> { Seg(1), Seg(2), Seg(4) }, a.MaxConcurrent.ToString(CultureInfo.InvariantCulture), v =>
            {
                int n;
                if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                {
                    a.MaxConcurrent = n;
                }
            });
            var logDays = new ComboBox();
            foreach (var n in new[] { 7, 30, 90 })
            {
                logDays.Items.Add(n.ToString(CultureInfo.InvariantCulture) + "일");
                if (a.LogDays == n)
                {
                    logDays.SelectedIndex = logDays.Items.Count - 1;
                }
            }

            logDays.SelectionChanged += (s, e) =>
            {
                var idx = logDays.SelectedIndex;
                a.LogDays = idx == 0 ? 7 : idx == 2 ? 90 : 30;
            };
            _content.Children.Add(Kit.FormGrid(3,
                Kit.Field("동시에 실행할 작업", concurrent, false, "작업 하나 = 에이전트 하나"),
                Kit.Field("실행 로그 보관", logDays, false, null)));
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            var agentPath = AppServices.DataDirectory != null
                ? System.IO.Path.Combine(AppServices.DataDirectory, "agent", ver != null ? ver.ToString(3) : "1.0.0", "MigrationAgent.exe")
                : "(데이터 폴더 미설정)";
            _content.Children.Add(Kit.KeyValue(new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("실행 파일", agentPath),
                new KeyValuePair<string, string>("통신", "이름 있는 파이프 folderss-migration-<RUN_ID> (현재 Windows 사용자만 접근)"),
                new KeyValuePair<string, string>("실행 중", "없음")
            }));
        }

        private static Kit.SegmentOption Seg(int n)
        {
            return new Kit.SegmentOption { Value = n.ToString(CultureInfo.InvariantCulture), Label = n.ToString(CultureInfo.InvariantCulture) };
        }

        private static ComboBox CommitSelect(int value, Action<int> onChange)
        {
            return SizeSelect(new[] { 1000, 10000, 50000 }, value, " rows / commit", onChange);
        }

        private static ComboBox FetchSelect(int value, Action<int> onChange)
        {
            return SizeSelect(new[] { 1000, 5000, 10000 }, value, " rows / fetch", onChange);
        }

        private static ComboBox SizeSelect(int[] sizes, int value, string suffix, Action<int> onChange)
        {
            var box = new ComboBox();
            var selected = 0;
            for (var i = 0; i < sizes.Length; i++)
            {
                box.Items.Add(sizes[i].ToString("N0", CultureInfo.GetCultureInfo("en-US")) + suffix);
                if (sizes[i] == value)
                {
                    selected = i;
                }
            }

            box.SelectedIndex = selected;
            box.SelectionChanged += (s, e) =>
            {
                var idx = box.SelectedIndex;
                if (idx >= 0 && idx < sizes.Length)
                {
                    onChange(sizes[idx]);
                }
            };
            return box;
        }

        private static MigrationSettings CloneSettings(MigrationSettings source)
        {
            return MigrationSettingsStore.Deserialize(MigrationSettingsStore.Serialize(source));
        }
    }
}
