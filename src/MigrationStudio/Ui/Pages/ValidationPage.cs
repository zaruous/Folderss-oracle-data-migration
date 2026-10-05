using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Validation;
using MigrationStudio.Logic;
using MigrationStudio.Services;

namespace MigrationStudio.Ui.Pages
{
    /// <summary>
    /// STEP 4 검증(UI-MIG-005). 실행 전 검증은 검사가 끝나는 대로 표에 한 줄씩 채우고(다시 그리기는 0.12초에 한 번으로 묶음),
    /// 실행 후 검증 탭은 실행을 마친 뒤(P6c)에 채운다.
    /// </summary>
    internal sealed class ValidationPage : PageFrame
    {
        private readonly IMappingUiHost _host;
        private readonly Func<IValidationService> _serviceFactory;
        private readonly Func<MigrationStudio.Core.Validation.IPostValidationRunner> _postFactory;
        private CancellationTokenSource _postCts;
        private readonly StackPanel _bodyPanel = new StackPanel();
        private readonly DispatcherTimer _renderTimer;
        private Button _runButton;
        private CancellationTokenSource _cts;
        private bool _renderPending;

        public ValidationPage(IMappingUiHost host, Func<IValidationService> serviceFactory, Func<MigrationStudio.Core.Validation.IPostValidationRunner> postFactory)
        {
            _host = host;
            _serviceFactory = serviceFactory;
            _postFactory = postFactory;
            SetStep(3, Labels.StepTitles[3], "실제 이관 전에 접속·객체·매핑·형식·제약·공간을 검사하고(PASS · WARN · ERROR), 이관 뒤에는 원본과 대상을 비교합니다.");
            SetBody(_bodyPanel);
            SetFooterHint("검사에 쓰는 SQL은 기능 설계서 UI-MIG-005에 정리");
            _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _renderTimer.Tick += (s, e) =>
            {
                if (_renderPending)
                {
                    _renderPending = false;
                    Rebuild();
                    _host.State.MarkUiChanged();
                }
            };
            Unloaded += (s, e) => _renderTimer.Stop();
            Rebuild();
        }

        public bool IsRunning
        {
            get { return _host.State.Pre != null && _host.State.Pre.Running; }
        }

        public void RefreshPartial(ChangeScope scope)
        {
            Rebuild();
        }

        /// <summary>실행 전 검증을 시작한다(F6·메뉴·버튼). 이미 돌고 있으면 무시.</summary>
        public async void Start()
        {
            var state = _host.State;
            if (IsRunning)
            {
                return;
            }

            if (!_host.Operations.HasMetadata())
            {
                Kit.Toast(_host.Owner, "메타데이터를 먼저 불러오세요", "warn");
                return;
            }

            var service = _serviceFactory();
            var pre = new PreValidationSession { Running = true, JobVersion = state.JobVersion, At = DateTime.Now };
            state.Pre = pre;
            state.Ui.ValTab = "pre";
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            var started = DateTime.UtcNow;
            _renderTimer.Start();
            Rebuild();
            state.MarkUiChanged();
            try
            {
                // UI 스레드에서 시작한다: 서비스가 비밀번호 입력 같은 UI를 띄울 수 있다(DB 작업은 서비스·엔진이 안에서 따로 돌린다)
                var items = await service.RunPreAsync(item =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (ReferenceEquals(state.Pre, pre) && pre.Running)
                        {
                            pre.Items.Add(item);
                            _renderPending = true;
                        }
                    }));
                }, ct).ConfigureAwait(true);
                if (items != null)
                {
                    pre.Items = items;
                }
            }
            catch (OperationCanceledException)
            {
                pre.Error = "검증을 취소했습니다.";
            }
            catch (Exception ex)
            {
                pre.Error = ex.Message;
            }
            finally
            {
                pre.Running = false;
                pre.ElapsedMs = (long)(DateTime.UtcNow - started).TotalMilliseconds;
                pre.At = DateTime.Now;
                _renderTimer.Stop();
                _renderPending = false;
                Rebuild();
                state.MarkUiChanged();
            }

            if (pre.Error == null)
            {
                var c = ValidationLogic.Count(pre.Items);
                Kit.Toast(_host.Owner, "검증 완료 · PASS " + c.Pass + " · WARN " + c.Warn + " · ERROR " + c.Error, c.Error > 0 ? "err" : c.Warn > 0 ? "warn" : "ok");
            }
            else
            {
                Kit.Toast(_host.Owner, pre.Error, "err");
            }
        }

        public bool IsPostRunning
        {
            get { return _host.State.Post != null && _host.State.Post.Running; }
        }

        /// <summary>실행 후 검증을 시작한다(실행이 끝난 뒤에만). Dry Run은 대상에 쓰지 않으므로 행 수 비교를 건너뛴다(서비스가 SKIP 항목을 돌려줌).</summary>
        public async void StartPost()
        {
            var state = _host.State;
            var run = state.Run;
            if (run == null || !ValidationLogic.CanStartPost(run.State))
            {
                Kit.Toast(_host.Owner, "실행을 마친 뒤에 검증할 수 있습니다", "warn");
                return;
            }

            if (IsPostRunning)
            {
                return;
            }

            var runner = _postFactory();
            var post = new PostValidationSession { RunId = run.RunId, Running = true, At = DateTime.Now };
            state.Post = post;
            state.Ui.ValTab = "post";
            _postCts = new CancellationTokenSource();
            var ct = _postCts.Token;
            _renderTimer.Start();
            Rebuild();
            try
            {
                var request = new PostValidationRequest { RunId = run.RunId, Dry = run.Dry, Final = run.Final ?? run.Snapshot };
                var items = await runner.RunPostAsync(request, item =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (ReferenceEquals(state.Post, post) && post.Running)
                        {
                            post.Items.Add(item);
                            _renderPending = true;
                        }
                    }));
                }, ct).ConfigureAwait(true);
                if (items != null)
                {
                    post.Items = items;
                }
            }
            catch (OperationCanceledException)
            {
                post.Error = "검증을 취소했습니다.";
            }
            catch (Exception ex)
            {
                post.Error = ex.Message;
            }
            finally
            {
                post.Running = false;
                post.At = DateTime.Now;
                _renderTimer.Stop();
                _renderPending = false;
                Rebuild();
            }

            if (post.Error == null)
            {
                var c = ValidationLogic.CountPost(post.Items);
                Kit.Toast(_host.Owner, "실행 후 검증 완료 · PASS " + c.Pass + " · WARN " + c.Warn + " · ERROR " + c.Error, c.Error > 0 ? "err" : c.Warn > 0 ? "warn" : "ok");
            }
            else
            {
                Kit.Toast(_host.Owner, post.Error, "err");
            }
        }

        public void CancelRunning()
        {
            if (_cts != null)
            {
                _cts.Cancel();
            }
        }

        // ================= 화면 =================

        private void Rebuild()
        {
            _runButton = Kit.PrimaryButton(IsRunning ? "검증 중…" : "실행 전 검증 (F6)", Icons.Checklist);
            _runButton.IsEnabled = !IsRunning;
            _runButton.MinWidth = 132;
            _runButton.Click += (s, e) => Start();
            var post = Kit.Button("실행 후 검증", Icons.Sync);
            post.IsEnabled = _host.State.Run != null && ValidationLogic.CanStartPost(_host.State.Run.State) && !IsPostRunning;
            post.ToolTip = post.IsEnabled ? "실행 결과를 원본과 대상에서 비교합니다" : "실행을 마친 뒤에 켜집니다";
            post.Click += (s, e) => StartPost();
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(_runButton);
            post.Margin = new Thickness(6, 0, 0, 0);
            actions.Children.Add(post);
            SetHeaderActions(actions);

            _bodyPanel.Children.Clear();
            var inner = new StackPanel();
            inner.Children.Add(BuildTabs());
            inner.Children.Add(_host.State.Ui.ValTab == "post" ? BuildPostTab() : BuildPreTab());
            var card = Kit.Card(null, null, inner, null);
            _bodyPanel.Children.Add(card);
        }

        private UIElement BuildTabs()
        {
            var pre = _host.State.Pre;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-12, -12, -12, 10) };
            var border = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(-12, -12, -12, 12), Child = row };
            border.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            row.Margin = new Thickness(0);
            row.Children.Add(Tab("실행 전 검증" + (pre != null ? " " + pre.Items.Count : ""), "pre"));
            row.Children.Add(Tab("실행 후 검증", "post"));
            return border;
        }

        private Button Tab(string label, string key)
        {
            var text = Theme.Text(label);
            Border underline;
            var button = WorkspaceUi.SelectorButton(text, out underline);
            WorkspaceUi.SetSelected(button, underline, _host.State.Ui.ValTab == key);
            button.Click += (s, e) =>
            {
                _host.State.Ui.ValTab = key;
                Rebuild();
            };
            return button;
        }

        private UIElement BuildPreTab()
        {
            var state = _host.State;
            var pre = state.Pre;
            if (pre == null)
            {
                var empty = Kit.EmptyState(Icons.Checklist, "아직 검증하지 않았습니다",
                    "접속 · 원본/대상 테이블 · 컬럼 매핑 · 형식 호환성 · NOT NULL · PK/Unique · 중복 키 · VARCHAR 길이 · NUMBER 정밀도 · 테이블스페이스를 검사합니다. 큰 테이블은 표본으로 잽니다.");
                var go = Kit.PrimaryButton("검증 실행 (F6)", Icons.Play);
                go.Click += (s, e) => Start();
                empty.Children.Add(go);
                return empty;
            }

            var panel = new StackPanel();
            var counts = ValidationLogic.Count(pre.Items);
            var onlyIssues = state.Ui.ValFilter == "issues";

            // 위 줄: 집계 칩 | 상태 · 마지막 검증 · 바뀜 표시 · 거르기
            var top = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            chips.Children.Add(Chip("PASS", counts.Pass));
            chips.Children.Add(Chip("WARN", counts.Warn));
            chips.Children.Add(Chip("ERROR", counts.Error));
            if (counts.Info > 0)
            {
                chips.Children.Add(Chip("INFO", counts.Info));
            }

            top.Children.Add(chips);
            var right = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            if (pre.Running)
            {
                var running = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 0) };
                running.Children.Add(Kit.Spinner());
                var t = Theme.Secondary("검사 중… " + pre.Items.Count);
                t.Margin = new Thickness(6, 0, 0, 0);
                running.Children.Add(t);
                right.Children.Add(running);
            }
            else
            {
                var last = Theme.Secondary(ValidationLogic.LastRunText(pre.At, pre.ElapsedMs));
                last.Margin = new Thickness(0, 0, 10, 0);
                last.VerticalAlignment = VerticalAlignment.Center;
                right.Children.Add(last);
                if (pre.JobVersion != state.JobVersion)
                {
                    var stale = Kit.Pill("warn", "검증 뒤 작업이 바뀜", Icons.Warn);
                    stale.Margin = new Thickness(0, 0, 10, 0);
                    right.Children.Add(stale);
                }
            }

            right.Children.Add(Kit.Segmented(new[]
            {
                new Kit.SegmentOption { Value = "all", Label = "전체" },
                new Kit.SegmentOption { Value = "issues", Label = "문제만" }
            }, onlyIssues ? "issues" : "all", v => { state.Ui.ValFilter = v; Rebuild(); }));
            Grid.SetColumn(right, 1);
            top.Children.Add(right);
            panel.Children.Add(top);

            if (pre.Running)
            {
                panel.Children.Add(ProgressBar(ValidationLogic.Progress(pre.Items.Count)));
            }

            if (pre.Error != null && !pre.Running)
            {
                var err = Kit.Notice("err", new List<Inline> { new Run(pre.Error) });
                err.Margin = new Thickness(0, 0, 0, 10);
                panel.Children.Add(err);
            }

            var selected = new HashSet<string>(state.Job.Mappings.Where(m => m.Use).Select(m => m.Id), StringComparer.Ordinal);
            var gate = ValidationLogic.Gate(pre.Items, selected, pre.Running);
            if (gate.Kind != "none" && pre.Error == null)
            {
                var inlines = new List<Inline> { new Run(gate.Lead) { FontWeight = FontWeights.SemiBold } };
                if (!string.IsNullOrEmpty(gate.Text))
                {
                    inlines.Add(new Run(gate.Text));
                }

                var notice = Kit.Notice(gate.Kind, inlines);
                notice.Margin = new Thickness(0, 0, 0, 10);
                if (gate.ShowRunLink)
                {
                    var linkPanel = new StackPanel { Orientation = Orientation.Horizontal };
                    var link = Kit.LinkButton("실행 화면으로 ›", () => _host.GoToStep(4));
                    linkPanel.Children.Add(link);
                    var wrap = new StackPanel();
                    wrap.Children.Add(notice);
                    notice.Margin = new Thickness(0);
                    var host = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
                    host.Children.Add(notice);
                    link.Margin = new Thickness(4, 4, 0, 0);
                    host.Children.Add(link);
                    panel.Children.Add(host);
                }
                else
                {
                    panel.Children.Add(notice);
                }
            }

            var items = ValidationLogic.Filter(pre.Items, onlyIssues);
            if (items.Count == 0 && !pre.Running)
            {
                panel.Children.Add(Kit.EmptyState(Icons.Check, onlyIssues ? "문제가 없습니다" : "검사 결과가 없습니다", ""));
                return panel;
            }

            panel.Children.Add(BuildTable(items, pre.Running));
            return panel;
        }

        private UIElement Chip(string level, int count)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
            row.Children.Add(Kit.LevelBadge(level, null));
            var n = Theme.Text(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            n.FontWeight = FontWeights.SemiBold;
            n.Margin = new Thickness(6, 0, 0, 0);
            n.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(n);
            return row;
        }

        private static UIElement ProgressBar(double percent)
        {
            var grid = new Grid { Height = 3, Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.01, percent), GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.01, 100 - percent), GridUnitType.Star) });
            var track = new Border { Tag = "overlay" };
            track.SetResourceReference(Border.BackgroundProperty, Theme.Border);
            Grid.SetColumnSpan(track, 2);
            grid.Children.Add(track);
            var fill = new Border { Tag = "overlay" };
            fill.SetResourceReference(Border.BackgroundProperty, Theme.Accent);
            grid.Children.Add(fill);
            return grid;
        }

        private UIElement BuildTable(IList<ValidationItem> items, bool running)
        {
            var grid = new RowGrid(
                new RowGridColumn { Header = "결과", Width = new GridLength(72) },
                new RowGridColumn { Header = "검사 항목", Width = new GridLength(132) },
                new RowGridColumn { Header = "대상", Width = new GridLength(176) },
                new RowGridColumn { Header = "내용", Width = new GridLength(1, GridUnitType.Star) },
                new RowGridColumn { Header = "조치", Width = new GridLength(86) });
            foreach (var g in ValidationLogic.GroupItems(items))
            {
                grid.AddGroupHeader(g.Name, g.Items.Count);
                foreach (var item in g.Items)
                {
                    grid.AddRow(RowCells(item), item, null, null);
                }
            }

            if (running)
            {
                var wait = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 0, 6) };
                wait.Children.Add(Kit.Spinner());
                var t = Theme.Secondary("다음 검사…");
                t.Margin = new Thickness(6, 0, 0, 0);
                wait.Children.Add(t);
                grid.Root.Children.Add(wait);
            }

            return grid.Root;
        }

        private FrameworkElement[] RowCells(ValidationItem item)
        {
            var badge = Kit.LevelBadge(item.Level, null);
            badge.VerticalAlignment = VerticalAlignment.Center;
            var check = Theme.Text(item.Check ?? "");
            check.FontWeight = FontWeights.SemiBold;
            check.TextWrapping = TextWrapping.Wrap;
            check.VerticalAlignment = VerticalAlignment.Center;
            var target = Theme.Text(item.Target ?? "");
            target.FontFamily = Theme.Mono;
            target.FontSize = 11.5;
            target.TextWrapping = TextWrapping.Wrap;
            target.VerticalAlignment = VerticalAlignment.Center;

            var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            detail.SetResourceReference(TextBlock.ForegroundProperty, Theme.PrimaryText);
            detail.Inlines.Add(new Run(item.Detail ?? ""));
            if (!string.IsNullOrEmpty(item.Sample))
            {
                var sample = new Run("  · " + item.Sample);
                sample.SetResourceReference(TextElement.ForegroundProperty, Theme.DisabledText);
                detail.Inlines.Add(sample);
            }

            FrameworkElement fix = new Border();
            if (item.Level != CheckLevels.Pass && item.Fix != null && ValidationLogic.FixLabel(item.Fix) != null)
            {
                var fixAction = item.Fix;
                var link = Kit.LinkButton(ValidationLogic.FixLabel(fixAction), () => GoFix(fixAction));
                link.VerticalAlignment = VerticalAlignment.Center;
                fix = link;
            }

            return new FrameworkElement[] { badge, check, target, detail, fix };
        }

        private void GoFix(FixAction fix)
        {
            var state = _host.State;
            if (!string.IsNullOrEmpty(fix.MappingId))
            {
                state.Ui.SelMapping = fix.MappingId;
            }

            if (fix.Page == "sql")
            {
                _host.OpenSqlEditor(fix.MappingId);
                return;
            }

            if (fix.Page == "columns" && !string.IsNullOrEmpty(fix.MappingId))
            {
                _host.GoToColumns(fix.MappingId);
                if (!string.IsNullOrEmpty(fix.Column))
                {
                    state.Ui.SelColumn = fix.Column;
                    state.MarkUiChanged();
                }

                return;
            }

            var step = ValidationLogic.FixStep(fix);
            if (step >= 0)
            {
                _host.GoToStep(step);
            }
        }

        private UIElement BuildPostTab()
        {
            var state = _host.State;
            var run = state.Run;
            if (run == null || RunLogic.IsActive(run.State) || run.State == RunStates.Idle)
            {
                var empty = Kit.EmptyState(Icons.Checklist, "실행을 마친 뒤에 확인합니다",
                    "행 수 · PK 누락 · 중복 키 · 샘플 데이터 · 해시 · NULL 수를 원본과 대상에서 비교합니다.");
                var go = Kit.Button("실행 화면으로", null);
                go.Click += (s, e) => _host.GoToStep(4);
                empty.Children.Add(go);
                return empty;
            }

            var post = state.Post;
            if (post == null || post.RunId != run.RunId)
            {
                var empty = Kit.EmptyState(Icons.Checklist, "실행 " + run.RunId + " 결과를 검증할 수 있습니다",
                    run.Dry ? "Dry Run은 대상에 쓰지 않아 행 수 비교를 건너뜁니다." : "대상에서 COUNT·해시 집계를 실행합니다(대상 부하가 적은 시간에 권장).");
                var go = Kit.PrimaryButton("실행 후 검증", Icons.Play);
                go.Click += (s, e) => StartPost();
                empty.Children.Add(go);
                return empty;
            }

            var panel = new StackPanel();
            var counts = ValidationLogic.CountPost(post.Items);
            var top = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            chips.Children.Add(Chip("PASS", counts.Pass));
            chips.Children.Add(Chip("WARN", counts.Warn));
            chips.Children.Add(Chip("ERROR", counts.Error));
            top.Children.Add(chips);
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (post.Running)
            {
                right.Children.Add(Kit.Spinner());
                var t = Theme.Secondary("비교 중…");
                t.Margin = new Thickness(6, 0, 0, 0);
                right.Children.Add(t);
            }
            else
            {
                var info = Theme.Secondary("실행 " + run.RunId + " · " + post.At.ToString("HH:mm:ss"));
                info.Margin = new Thickness(0, 0, 10, 0);
                info.VerticalAlignment = VerticalAlignment.Center;
                right.Children.Add(info);
                var again = Kit.Button("다시 검증", Icons.Sync);
                again.Click += (s, e) => StartPost();
                right.Children.Add(again);
            }

            Grid.SetColumn(right, 1);
            top.Children.Add(right);
            panel.Children.Add(top);
            if (post.Error != null && !post.Running)
            {
                var err = Kit.Notice("err", new List<Inline> { new Run(post.Error) });
                err.Margin = new Thickness(0, 0, 0, 10);
                panel.Children.Add(err);
            }

            var grid = new RowGrid(
                new RowGridColumn { Header = "결과", Width = new GridLength(78) },
                new RowGridColumn { Header = "검사", Width = new GridLength(150) },
                new RowGridColumn { Header = "원본", Width = new GridLength(120), HeaderAlign = TextAlignment.Right },
                new RowGridColumn { Header = "대상", Width = new GridLength(170), HeaderAlign = TextAlignment.Right },
                new RowGridColumn { Header = "내용", Width = new GridLength(1, GridUnitType.Star) });
            foreach (var g in ValidationLogic.GroupPost(post.Items))
            {
                grid.AddGroupHeader(g.Name, -1);
                foreach (var item in g.Items)
                {
                    var badge = Kit.LevelBadge(item.Level, ValidationLogic.PostBadgeText(item));
                    badge.VerticalAlignment = VerticalAlignment.Center;
                    var check = Theme.Text(item.Check ?? "");
                    check.FontWeight = FontWeights.SemiBold;
                    check.VerticalAlignment = VerticalAlignment.Center;
                    var src = Theme.Text(item.Source ?? "");
                    src.FontFamily = Theme.Mono;
                    src.FontSize = 12;
                    src.TextAlignment = TextAlignment.Right;
                    src.VerticalAlignment = VerticalAlignment.Center;
                    var tgt = Theme.Text(item.Target ?? "");
                    tgt.FontFamily = Theme.Mono;
                    tgt.FontSize = 12;
                    tgt.TextAlignment = TextAlignment.Right;
                    tgt.TextWrapping = TextWrapping.Wrap;
                    tgt.VerticalAlignment = VerticalAlignment.Center;
                    var detail = Theme.Text(item.Detail ?? "");
                    detail.FontSize = 12;
                    detail.TextWrapping = TextWrapping.Wrap;
                    detail.VerticalAlignment = VerticalAlignment.Center;
                    grid.AddRow(new FrameworkElement[] { badge, check, src, tgt, detail }, item, null, null);
                }
            }

            if (post.Running)
            {
                var wait = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 0, 6) };
                wait.Children.Add(Kit.Spinner());
                var t = Theme.Secondary("다음 검사…");
                t.Margin = new Thickness(6, 0, 0, 0);
                wait.Children.Add(t);
                grid.Root.Children.Add(wait);
            }

            panel.Children.Add(grid.Root);
            return panel;
        }
    }
}
