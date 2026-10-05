using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Hosting;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Validation;
using MigrationStudio.Logic;
using MigrationStudio.Services;
using MigrationStudio.Ui.Modals;

namespace MigrationStudio.Ui.Pages
{
    /// <summary>
    /// STEP 5 실행(UI-MIG-006). 구조(카드)는 상태가 바뀔 때만 다시 만들고, 실행 중 갱신은 만들어 둔 칸의 값만 바꾼다
    /// (엔진 이벤트는 RunPresenter가 0.1초에 한 번으로 묶어 준다).
    /// </summary>
    internal sealed class RunPage : PageFrame
    {
        private readonly IMappingUiHost _host;
        private readonly RunPresenter _presenter;
        private readonly Func<IRunService> _serviceFactory;
        private readonly StackPanel _bodyPanel = new StackPanel();
        private CancellationTokenSource _startCts;

        // 갱신할 칸들(Rebuild에서 만든다)
        private TextBlock _rowsText, _ofText, _pctText, _currentText, _runIdText;
        private Border _statePill;
        private ProgressBarView _bar;
        private readonly Dictionary<string, TextBlock> _stats = new Dictionary<string, TextBlock>();
        private readonly List<StageCard> _stages = new List<StageCard>();
        private StackPanel _taskHost;
        private StackPanel _logPanel;
        private ScrollViewer _logScroll;
        private int _logShown;
        private int _logCleared;
        private Border _cpHost;
        private DateTime _lastCheckpointDraw = DateTime.MinValue;
        private Button _startButton, _pauseButton, _resumeButton, _stopButton;

        private sealed class StageCard
        {
            public TextBlock Metric { get; set; }
            public TextBlock Sub { get; set; }
            public ProgressBarView Bar { get; set; }
            public Border Frame { get; set; }
        }

        public RunPage(IMappingUiHost host, RunPresenter presenter, Func<IRunService> serviceFactory)
        {
            _host = host;
            _presenter = presenter;
            _serviceFactory = serviceFactory;
            SetStep(4, Labels.StepTitles[4], "작업을 골라 Dry Run으로 확인한 뒤 이관합니다. 일시정지는 커밋 경계에서 멈추고, 중지하면 진행 중 배치를 롤백한 뒤 마지막 커밋 키를 체크포인트로 남깁니다.");
            // 위(작업 선택·실행 제어·진행)는 스크롤, 아래(로그·체크포인트)는 분할선으로 높이를 나눈다 — 창 높이를 채우는 본문
            _topScroll = new ScrollViewer { Content = _bodyPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            // 아래 패널은 로그 카드(머리 40 + 본문)와 좁을 때 그 아래 붙는 체크포인트 카드까지 들어가야 하므로 200px은 확보한다
            _split = new SplitPane(State.Ui.RunSplit, r => State.Ui.RunSplit = r, 140, 220) { Top = _topScroll };
            SetBodyFill(_split);
            SetFooterHint("F5 시작 · 일시정지 중 F5 = 이어서");
            _presenter.StateChanged += () => Rebuild();
            _presenter.Render += OnRender;
            Rebuild();
        }

        private StudioState State { get { return _host.State; } }

        private RunView View { get { return State.Run; } }

        private string CurrentState { get { return View != null ? View.State : RunStates.Idle; } }

        // ================= 선택·모드 =================

        private HashSet<string> Selection()
        {
            var ui = State.Ui;
            var all = State.Job.Mappings ?? new List<Mapping>();
            if (ui.RunSelected == null)
            {
                ui.RunSelected = new HashSet<string>(StringComparer.Ordinal);
                ui.RunKnown = new HashSet<string>(StringComparer.Ordinal);
            }

            if (ui.RunKnown == null)
            {
                ui.RunKnown = new HashSet<string>(ui.RunSelected, StringComparer.Ordinal);
            }

            // 새 매핑은 기본으로 고르고 사라진 매핑은 뺀다 — 실행 화면이 매핑보다 먼저 만들어져도(창을 열 때 다시 붙기 검사) 선택이 비지 않게
            RunLogic.SyncSelection(ui.RunSelected, ui.RunKnown, all);
            return ui.RunSelected;
        }

        private List<Mapping> Ordered()
        {
            var all = State.Job.Mappings ?? new List<Mapping>();
            var meta = State.TargetMeta();
            return meta != null ? RunPlanner.OrderByFk(all, meta) : all.ToList();
        }

        // ================= 화면 구조 =================

        public void RefreshPartial(ChangeScope scope)
        {
            if (!RunLogic.IsActive(CurrentState))
            {
                Rebuild();
            }
        }

        // ---------- 다시 붙기 ----------

        private List<AgentRunInfo> _aliveRuns = new List<AgentRunInfo>();
        private readonly SplitPane _split;
        private readonly ScrollViewer _topScroll;

        /// <summary>
        /// 창을 열 때 한 번: 살아 있는(끝나지 않은) 에이전트가 있으면 같은 작업은 바로 다시 붙고, 다른 작업은 실행 화면에 안내 상자를 띄운다.
        /// 에이전트는 창과 무관하게 돌기 때문에 이것이 없으면 "창을 닫아도 계속"이 반쪽이 된다.
        /// </summary>
        public void CheckAliveRuns()
        {
            if (State.Run != null && RunLogic.IsActive(CurrentState))
            {
                return;
            }

            List<AgentRunInfo> all;
            try
            {
                all = _serviceFactory().ListAlive();
                _aliveRuns = RunLogic.AliveForReattach(all);
            }
            catch (Exception ex)
            {
                UiTrace.Write("reattach", "ListAlive 실패: " + ex.Message);
                _aliveRuns = new List<AgentRunInfo>();
                return;
            }

            UiTrace.Write("reattach", "살아 있는 에이전트 " + (all != null ? all.Count : 0) + "개, 끝나지 않은 것 " + _aliveRuns.Count + "개"
                + (all != null && all.Count > 0 ? " — " + string.Join(", ", all.Select(r => r.RunId + "(" + r.State + ", pid " + r.Pid + ", " + r.JobName + ")")) : ""));
            if (_aliveRuns.Count == 0)
            {
                return;
            }

            var mine = RunLogic.PickReattach(_aliveRuns, State.Job != null ? State.Job.JobName : null);
            if (mine != null)
            {
                UiTrace.Write("reattach", "같은 작업 '" + State.Job.JobName + "' → " + mine.RunId + "에 바로 다시 붙음");
                Reattach(mine);
                return;
            }

            UiTrace.Write("reattach", "같은 작업 없음(현재 '" + (State.Job != null ? State.Job.JobName : "") + "') → 실행 화면에 안내");

            Rebuild();
            Kit.Toast(_host.Owner, "진행 중인 실행 " + _aliveRuns.Count + "개 — 실행 화면에서 다시 붙을 수 있습니다", "warn");
        }

        private async void Reattach(AgentRunInfo run)
        {
            var st = State.Job.Strategy ?? new MigrationStrategy();
            var view = _presenter.Begin("EXECUTE", st.FetchSize, st.CommitSize, st.Workers);
            view.Stage = "실행 " + run.RunId + "에 다시 붙는 중…";
            Rebuild();
            try
            {
                var client = await _serviceFactory().AttachAsync(run, CancellationToken.None).ConfigureAwait(true);
                _presenter.Attach(client, "EXECUTE", true);
                _aliveRuns = new List<AgentRunInfo>();
                _host.GoToStep(4);
                UiTrace.Write("reattach", run.RunId + " 다시 붙음(pid " + client.Pid + ")");
                Kit.Toast(_host.Owner, "실행 " + run.RunId + "에 다시 붙었습니다", "ok");
            }
            catch (Exception ex)
            {
                UiTrace.Write("reattach", run.RunId + " 다시 붙기 실패: " + ex);
                _presenter.StartFailed("다시 붙지 못했습니다: " + ex.Message);
            }
        }

        private UIElement BuildReattachNotice()
        {
            var runs = RunLogic.AliveForReattach(_aliveRuns);
            if (runs.Count == 0 || (State.Run != null && RunLogic.IsActive(CurrentState)))
            {
                return null;
            }

            var panel = new StackPanel();
            var notice = Kit.Notice("warn", new List<Inline> { new Run(RunLogic.ReattachNoticeText(runs)) });
            panel.Children.Add(notice);
            var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var run in runs)
            {
                var target = run;
                var btn = Kit.Button("다시 붙기 · " + run.RunId, Icons.Link);
                btn.Margin = new Thickness(0, 0, 6, 0);
                btn.Click += (s, e) => Reattach(target);
                buttons.Children.Add(btn);
            }

            panel.Children.Add(buttons);
            return panel;
        }

        private void Rebuild()
        {
            _stats.Clear();
            _stages.Clear();
            _logShown = 0;
            _bodyPanel.Children.Clear();
            var reattach = BuildReattachNotice();
            if (reattach != null)
            {
                reattach.SetValue(MarginProperty, new Thickness(0, 0, 0, 14));
                _bodyPanel.Children.Add(reattach);
            }

            var notice = BuildResultNotice();
            if (notice != null)
            {
                notice.Margin = new Thickness(0, 0, 0, 14);
                _bodyPanel.Children.Add(notice);
            }

            var top = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var jobs = BuildJobsCard();
            var control = BuildControlCard();
            top.Children.Add(jobs);
            Grid.SetColumn(control, 1);
            top.Children.Add(control);
            top.SizeChanged += (s, e) =>
            {
                var stacked = e.NewSize.Width < 900;
                jobs.Margin = stacked ? new Thickness(0, 0, 0, 14) : new Thickness(0, 0, 14, 0);
                Grid.SetColumn(control, stacked ? 0 : 1);
                Grid.SetRow(control, stacked ? 1 : 0);
                Grid.SetColumnSpan(jobs, stacked ? 2 : 1);
                Grid.SetColumnSpan(control, stacked ? 2 : 1);
            };
            _bodyPanel.Children.Add(top);

            var progress = BuildProgressCard();
            progress.Margin = new Thickness(0, 0, 0, 14);
            _bodyPanel.Children.Add(progress);

            // 아래 패널: 로그 카드가 분할선 아래의 높이를 채우고(별 행), 체크포인트는 옆(넓을 때) 또는 아래(좁을 때)
            var bottom = new Grid();
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
            bottom.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 100 });
            bottom.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var log = BuildLogCard();
            _cpHost = new Border();
            bottom.Children.Add(log);
            Grid.SetColumn(_cpHost, 1);
            bottom.Children.Add(_cpHost);
            bottom.SizeChanged += (s, e) =>
            {
                var stacked = e.NewSize.Width < 900;
                bottom.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(340);
                log.Margin = stacked ? new Thickness(0, 0, 0, 14) : new Thickness(0, 0, 14, 0);
                Grid.SetColumn(_cpHost, stacked ? 0 : 1);
                Grid.SetRow(_cpHost, stacked ? 1 : 0);
                Grid.SetColumnSpan(_cpHost, stacked ? 2 : 1);
                Grid.SetColumnSpan(log, stacked ? 2 : 1);
                // 좁을 때는 로그(2)·체크포인트(1)가 분할선 아래 높이를 비율로 나눈다 — 고정 높이면 작은 창에서 로그가 0px이 된다
                bottom.RowDefinitions[1].Height = stacked ? new GridLength(2, GridUnitType.Star) : GridLength.Auto;
                bottom.RowDefinitions[1].MinHeight = stacked ? 70 : 0;
            };
            _split.Bottom = bottom;

            DrawCheckpoints();
            UpdateLive(true);
        }

        private FrameworkElement BuildResultNotice()
        {
            var view = View;
            if (view == null)
            {
                return null;
            }

            var snap = view.Final ?? view.Snapshot;
            var written = snap != null ? snap.Tasks.Sum(t => t.Written) : 0;
            var rejected = snap != null ? snap.Tasks.Sum(t => t.Rejected) : 0;
            var elapsed = snap != null ? snap.Elapsed : 0;
            var tail = RunLogic.Duration(elapsed) + " · 처리 " + RunLogic.N(written) + "행 · 거부 " + RunLogic.N(rejected) + "행   ";
            if (view.State == RunStates.Done || view.State == RunStates.Stopped || view.State == RunStates.Failed)
            {
                var lead = view.State == RunStates.Done ? (view.Dry ? "Dry Run 완료 · " : "이관 완료 · ") : view.State == RunStates.Stopped ? "중지됨 · " : "실패 · ";
                var inlines = new List<Inline> { new Run(lead) { FontWeight = FontWeights.SemiBold }, new Run(tail) };
                if (view.State == RunStates.Failed && !string.IsNullOrEmpty(view.Error))
                {
                    inlines.Add(new Run(view.Error.Split('\n')[0] + "   "));
                }

                var notice = Kit.Notice(view.State == RunStates.Done ? "ok" : view.State == RunStates.Stopped ? "warn" : "err", inlines);
                var host = new StackPanel();
                host.Children.Add(notice);
                var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                if (view.State == RunStates.Done && !view.Dry)
                {
                    links.Children.Add(Kit.LinkButton("실행 후 검증 ›", () => { State.Ui.ValTab = "post"; _host.GoToStep(3); }));
                }

                if (view.State != RunStates.Done)
                {
                    var resume = Kit.LinkButton("체크포인트에서 재개 ›", () => StartRun("RESUME"));
                    resume.Margin = new Thickness(view.State == RunStates.Done ? 0 : 0, 0, 12, 0);
                    links.Children.Add(resume);
                }

                if (links.Children.Count > 0)
                {
                    host.Children.Add(links);
                }

                return host;
            }

            if (view.State == RunStates.Detached)
            {
                var notice = Kit.Notice("warn", new List<Inline>
                {
                    new Run("에이전트와 연결이 끊겼습니다 ") { FontWeight = FontWeights.SemiBold },
                    new Run("(에이전트는 계속 실행 중일 수 있습니다) " + (view.Error ?? ""))
                });
                return notice;
            }

            if (view.State == RunStates.Idle && !string.IsNullOrEmpty(view.Error))
            {
                return Kit.Notice("err", new List<Inline> { new Run("실행을 시작하지 못했습니다 · ") { FontWeight = FontWeights.SemiBold }, new Run(view.Error) });
            }

            return null;
        }

        // ---------- 작업 선택 ----------

        private FrameworkElement BuildJobsCard()
        {
            var active = RunLogic.IsActive(CurrentState);
            var selection = Selection();
            var ordered = Ordered();
            var list = new StackPanel();
            var order = 0;
            long total = 0;
            foreach (var m in ordered)
            {
                var on = selection.Contains(m.Id);
                var src = State.SourceOf(m);
                var rows = src != null ? src.Rows : null;
                if (on && rows != null)
                {
                    total += rows.Value;
                }

                var mapping = m;
                var box = new CheckBox { IsChecked = on, IsEnabled = !active, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0) };
                box.Click += (s, e) =>
                {
                    if (box.IsChecked == true)
                    {
                        selection.Add(mapping.Id);
                    }
                    else
                    {
                        selection.Remove(mapping.Id);
                    }

                    State.MarkUiChanged();
                    Rebuild();
                };
                var item = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                item.Children.Add(box);
                var num = Theme.Secondary(on ? (++order).ToString() : "–");
                num.FontFamily = Theme.Mono;
                num.Margin = new Thickness(0, 2, 0, 0);
                Grid.SetColumn(num, 1);
                item.Children.Add(num);

                var text = new StackPanel();
                var title = new StackPanel { Orientation = Orientation.Horizontal };
                if (m.IsSql)
                {
                    var tag = Kit.SqlTag("SQL");
                    tag.Margin = new Thickness(0, 0, 6, 0);
                    title.Children.Add(tag);
                }

                var name = Theme.Text(m.Source + " → " + (m.Target ?? "?"));
                name.FontWeight = FontWeights.SemiBold;
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                title.Children.Add(name);
                text.Children.Add(title);
                var meta = new WrapPanel();
                var rowsText = Theme.Secondary(rows != null ? (m.IsSql ? "~" : "") + RunLogic.N(rows.Value) + " 행" : "행 수 모름");
                rowsText.Margin = new Thickness(0, 0, 10, 0);
                meta.Children.Add(rowsText);
                var modeText = Theme.Secondary(WriteModes.Of(m.Mode).Label);
                modeText.Margin = new Thickness(0, 0, 10, 0);
                meta.Children.Add(modeText);
                CheckpointInfo cp;
                if (State.Job.Checkpoints != null && State.Job.Checkpoints.TryGetValue(m.Id, out cp) && cp != null)
                {
                    TextBlock cpText;
                    if (cp.Status == "done")
                    {
                        cpText = Theme.Secondary("지난 실행 완료");
                        cpText.Foreground = Theme.Success;
                    }
                    else
                    {
                        cpText = Theme.Secondary("↻ " + cp.Column + " " + cp.Value + " (" + Math.Floor(RunLogic.Pct(cp.Rows, cp.Total)) + "%)");
                        cpText.Foreground = Theme.Warning;
                    }

                    cpText.ToolTip = cp.At;
                    meta.Children.Add(cpText);
                }

                foreach (TextBlock t in meta.Children.OfType<TextBlock>())
                {
                    t.FontSize = 11.5;
                }

                text.Children.Add(meta);
                Grid.SetColumn(text, 2);
                item.Children.Add(text);

                var errs = State.Pre != null ? State.Pre.Items.Count(i => i.Level == CheckLevels.Error && i.MappingId == m.Id) : 0;
                if (errs > 0)
                {
                    var badge = Kit.LevelBadge("ERROR", "ERROR " + errs);
                    Grid.SetColumn(badge, 3);
                    item.Children.Add(badge);
                }

                list.Children.Add(item);
            }

            UIElement body;
            if (ordered.Count == 0)
            {
                var empty = Kit.EmptyState(Icons.List, "실행할 매핑이 없습니다", "");
                var go = Kit.Button("테이블 매핑으로", null);
                go.Click += (s, e) => _host.GoToStep(1);
                empty.Children.Add(go);
                body = empty;
            }
            else
            {
                body = new ScrollViewer { MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = list };
            }

            var all = new CheckBox { Content = "모든 작업 (ALL SELECTED)", IsChecked = ordered.Count > 0 && ordered.All(m => selection.Contains(m.Id)), IsEnabled = !active };
            all.Click += (s, e) =>
            {
                selection.Clear();
                if (all.IsChecked == true)
                {
                    foreach (var m in ordered)
                    {
                        selection.Add(m.Id);
                    }
                }

                State.MarkUiChanged();
                Rebuild();
            };
            // POC의 보기 문장 대신 실제로 돌 순서 — 고른 작업을 대상 외래 키 기준(부모 → 자식)으로 정렬한 것
            var picked = ordered.Where(m => selection.Contains(m.Id)).ToList();
            var orderText = picked.Count == 0
                ? "실행 순서: 고른 작업이 없습니다"
                : "실행 순서(대상 외래 키 기준 부모 → 자식): " + string.Join(" → ", picked.Select(m => m.Target));
            var foot = Theme.Secondary(orderText);
            foot.FontSize = 12;
            foot.TextTrimming = TextTrimming.CharacterEllipsis;
            foot.ToolTip = orderText;
            var card = Kit.Card(Icons.List + "  작업 선택 · " + selection.Count + "개 · " + RunLogic.N(total) + " 행", all, body, foot);
            return card;
        }

        // ---------- 실행 제어 ----------

        private FrameworkElement BuildControlCard()
        {
            var state = CurrentState;
            var mode = State.Ui.RunMode ?? "EXECUTE";
            var st = State.Job.Strategy ?? new MigrationStrategy();
            var body = new StackPanel();

            var modeSeg = Kit.Segmented(new[]
            {
                new Kit.SegmentOption { Value = "DRY", Label = "Dry Run" },
                new Kit.SegmentOption { Value = "EXECUTE", Label = "이관 실행" },
                new Kit.SegmentOption { Value = "RESUME", Label = "체크포인트에서 재개" }
            }, mode, v => { State.Ui.RunMode = v; Rebuild(); });
            modeSeg.IsEnabled = !RunLogic.IsActive(state);
            var modeField = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            modeField.Children.Add(Kit.SectionLabel("실행 모드 (Run Mode)"));
            modeField.Children.Add(modeSeg);
            var desc = Theme.Secondary(RunLogic.ModeDescription(mode));
            desc.TextWrapping = TextWrapping.Wrap;
            desc.FontSize = 11.5;
            desc.Margin = new Thickness(0, 6, 0, 0);
            modeField.Children.Add(desc);
            body.Children.Add(modeField);

            var canStart = State.Job.Mappings != null && State.Job.Mappings.Count > 0;
            var controls = RunLogic.Controls(state, canStart);
            _startButton = Kit.PrimaryButton(RunLogic.StartButtonText(mode), mode == "DRY" ? Icons.View : mode == "RESUME" ? Icons.Resume : Icons.Play);
            _startButton.IsEnabled = controls.CanStart;
            _startButton.ToolTip = "F5";
            _startButton.Click += (s, e) => StartRun(mode);
            _pauseButton = Kit.Button("일시정지", Icons.Pause);
            _pauseButton.IsEnabled = controls.CanPause;
            _pauseButton.Click += (s, e) => _presenter.Pause();
            _resumeButton = Kit.Button("이어서", Icons.Play);
            _resumeButton.IsEnabled = controls.CanResume;
            _resumeButton.Click += (s, e) => _presenter.Resume();
            _stopButton = Kit.Button("중지", Icons.Stop);
            _stopButton.IsEnabled = controls.CanStop;
            _stopButton.Click += (s, e) => _presenter.Stop();
            var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            foreach (var b in new[] { _startButton, _pauseButton, _resumeButton, _stopButton })
            {
                b.Margin = new Thickness(0, 0, 6, 6);
                buttons.Children.Add(b);
            }

            body.Children.Add(buttons);
            var gate = BuildGateNotice();
            if (gate != null)
            {
                gate.Margin = new Thickness(0, 0, 0, 12);
                body.Children.Add(gate);
            }

            var rows = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("배치", "커밋 " + RunLogic.N(st.CommitSize) + "행 · Fetch " + RunLogic.N(st.FetchSize) + "행 · 작업자 " + st.Workers),
                new KeyValuePair<string, string>("오류 정책", RunLogic.PolicyText(st.ErrorPolicy) + (st.ErrorPolicy == "CONTINUE" ? " (" + st.ErrorTable + "<대상>)" : "")),
                new KeyValuePair<string, string>("대상", (State.ProfileForRole(Roles.Target) != null ? State.ProfileForRole(Roles.Target).Name : "—") + " · " + (State.Job.Target != null ? State.Job.Target.Schema : "")),
                new KeyValuePair<string, string>("체크포인트", CheckpointStoreText()),
                new KeyValuePair<string, string>("실행 위치", "하위 프로세스 MigrationAgent.exe · " + (State.Settings != null && State.Settings.Agent != null && State.Settings.Agent.OnHostExit == "STOP" ? "Folderss를 닫으면 함께 중지" : "창·Folderss를 닫아도 계속"))
            };
            var kv = Kit.KeyValue(rows);
            kv.Margin = new Thickness(0, 4, 0, 0);
            body.Children.Add(kv);
            return Kit.Card(Icons.Play + "  실행 제어", null, body, null);
        }

        private string CheckpointStoreText()
        {
            var store = State.Settings != null && State.Settings.Defaults != null ? State.Settings.Defaults.CheckpointStore : "AUTO";
            switch (store)
            {
                case "TARGET": return "대상 DB 제어 테이블(MIG_CHECKPOINT)";
                case "LOCAL": return "로컬 파일";
                default: return "자동 — 대상에 만들 수 있으면 대상 DB, 아니면 로컬 파일";
            }
        }

        private FrameworkElement BuildGateNotice()
        {
            var pre = State.Pre;
            if (pre == null)
            {
                var n = Kit.Notice("warn", new List<Inline> { new Run("실행 전 검증을 아직 하지 않았습니다. ") });
                return WithLink(n, "검증 실행 (F6)", () => _host.RunValidation());
            }

            if (pre.Running)
            {
                return Kit.Notice("info", new List<Inline> { new Run("검증 중…") });
            }

            var gate = ValidationGate.Evaluate(pre.Items, Selection());
            if (gate.Blocked)
            {
                var n = Kit.Notice("err", new List<Inline>
                {
                    new Run("고른 작업에 검증 ERROR " + gate.Errors + "건 ") { FontWeight = FontWeights.SemiBold },
                    new Run("— 이관 실행은 막힘, Dry Run은 가능. ")
                });
                return WithLink(n, "검증 결과 ›", () => _host.GoToStep(3));
            }

            if (pre.JobVersion != State.JobVersion)
            {
                return Kit.Notice("warn", new List<Inline> { new Run("검증한 뒤 작업이 바뀌었습니다 — 다시 검증을 권합니다") });
            }

            return Kit.Notice("ok", new List<Inline> { new Run("검증 통과 · " + pre.At.ToString("HH:mm:ss")) });
        }

        private static FrameworkElement WithLink(FrameworkElement notice, string linkText, Action click)
        {
            var host = new StackPanel();
            host.Children.Add(notice);
            var link = Kit.LinkButton(linkText, click);
            link.Margin = new Thickness(4, 4, 0, 0);
            host.Children.Add(link);
            return host;
        }

        // ---------- 진행 ----------

        private FrameworkElement BuildProgressCard()
        {
            var body = new StackPanel();
            _runIdText = Theme.Secondary("");
            _runIdText.FontSize = 12;
            _runIdText.Margin = new Thickness(0, 0, 10, 0);
            _runIdText.VerticalAlignment = VerticalAlignment.Center;
            _statePill = new Border { VerticalAlignment = VerticalAlignment.Center };
            var tools = new StackPanel { Orientation = Orientation.Horizontal };
            tools.Children.Add(_runIdText);
            tools.Children.Add(_statePill);

            var big = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            _pctText = Theme.Text("0%");
            _pctText.FontSize = 18;
            _pctText.FontWeight = FontWeights.SemiBold;
            DockPanel.SetDock(_pctText, Dock.Right);
            big.Children.Add(_pctText);
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            _rowsText = Theme.Text("0");
            _rowsText.FontSize = 24;
            _rowsText.FontWeight = FontWeights.SemiBold;
            _ofText = Theme.Secondary("/ 0 행");
            _ofText.Margin = new Thickness(8, 0, 0, 3);
            _ofText.VerticalAlignment = VerticalAlignment.Bottom;
            left.Children.Add(_rowsText);
            left.Children.Add(_ofText);
            big.Children.Add(left);
            body.Children.Add(big);
            _currentText = Theme.Secondary("");
            _currentText.FontSize = 12;
            _currentText.Margin = new Thickness(0, 0, 0, 8);
            body.Children.Add(_currentText);
            _bar = new ProgressBarView(8);
            _bar.Root.Margin = new Thickness(0, 0, 0, 12);
            body.Children.Add(_bar.Root);

            var stats = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 14) };
            foreach (var key in new[] { "rate:처리 속도", "elapsed:경과", "eta:남은 시간", "commits:커밋", "ins:Inserted", "upd:Updated", "rej:Rejected" })
            {
                var parts = key.Split(':');
                var cell = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
                var label = Theme.Secondary(parts[1]);
                label.FontSize = 11;
                var value = Theme.Text("—");
                value.FontSize = 15;
                value.FontWeight = FontWeights.SemiBold;
                cell.Children.Add(label);
                cell.Children.Add(value);
                _stats[parts[0]] = value;
                stats.Children.Add(cell);
            }

            body.Children.Add(stats);
            body.Children.Add(Kit.SectionLabel("엔진 파이프라인 — Streaming · Batch · Checkpoint (전체를 메모리에 올리지 않음)"));
            var pipe = new UniformGrid { Columns = 6, Margin = new Thickness(0, 0, 0, 14) };
            foreach (var name in new[] { "원본 읽기", "Transform", "컬럼 매핑", "배치 쓰기", "커밋", "체크포인트" })
            {
                var card = new StageCard
                {
                    Metric = Theme.Text("—"),
                    Sub = Theme.Secondary(""),
                    Bar = new ProgressBarView(3)
                };
                card.Metric.FontSize = 12.5;
                card.Metric.FontWeight = FontWeights.SemiBold;
                card.Metric.TextTrimming = TextTrimming.CharacterEllipsis;
                card.Sub.FontSize = 11;
                card.Sub.TextTrimming = TextTrimming.CharacterEllipsis;
                var title = Theme.Secondary(name);
                title.FontSize = 11.5;
                var stack = new StackPanel();
                stack.Children.Add(title);
                stack.Children.Add(card.Metric);
                stack.Children.Add(card.Sub);
                card.Bar.Root.Margin = new Thickness(0, 6, 0, 0);
                stack.Children.Add(card.Bar.Root);
                card.Frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 6, 0), Child = stack };
                card.Frame.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
                _stages.Add(card);
                pipe.Children.Add(card.Frame);
            }

            body.Children.Add(pipe);
            _taskHost = new StackPanel();
            body.Children.Add(_taskHost);
            return Kit.Card(Icons.Sync + "  진행", tools, body, null);
        }

        private FrameworkElement BuildLogCard()
        {
            _logPanel = new StackPanel();
            _logScroll = new ScrollViewer
            {
                MinHeight = 50,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _logPanel
            };
            var filter = Kit.Segmented(new[]
            {
                new Kit.SegmentOption { Value = "all", Label = "전체" },
                new Kit.SegmentOption { Value = "info", Label = "INFO" },
                new Kit.SegmentOption { Value = "warn", Label = "WARN" },
                new Kit.SegmentOption { Value = "error", Label = "ERROR" }
            }, State.Ui.LogFilter ?? "all", v => { State.Ui.LogFilter = v; _logShown = 0; _logPanel.Children.Clear(); AppendLog(); });
            var copy = Kit.IconButton(Icons.Copy, "로그 복사");
            copy.Click += (s, e) =>
            {
                if (View != null)
                {
                    DialogKit.TryCopy(string.Join("\n", View.Log.Select(l => l.At.ToString("HH:mm:ss") + " [" + l.Tag + "] " + l.Text)));
                }
            };
            var clear = Kit.IconButton(Icons.Delete, "화면에서 지우기");
            clear.Click += (s, e) =>
            {
                _logCleared = View != null ? View.Log.Count : 0;
                _logShown = 0;
                _logPanel.Children.Clear();
            };
            var tools = new StackPanel { Orientation = Orientation.Horizontal };
            tools.Children.Add(filter);
            tools.Children.Add(copy);
            tools.Children.Add(clear);
            // 로그는 분할선 아래 높이를 채운다(카드 본문이 DockPanel의 마지막 자식이라 늘어난다) — 높이는 분할선으로 조절
            var host = new Border { Padding = new Thickness(0), Child = _logScroll };
            return Kit.Card(Icons.List + "  Migration Log", tools, host, null);
        }

        // ================= 갱신 =================

        private void OnRender(int newLog)
        {
            UpdateLive(false);
        }

        private void UpdateLive(bool force)
        {
            var view = View;
            var state = view != null ? view.State : RunStates.Idle;
            var snap = view != null ? view.Snapshot : null;
            if (_rowsText == null)
            {
                return;
            }

            long total = 0;
            long done = 0;
            if (snap != null && snap.Totals != null && snap.Totals.Total > 0)
            {
                total = snap.Totals.Total;
                done = snap.Totals.Done;
            }
            else
            {
                var sel = Selection();
                foreach (var m in Ordered().Where(m => sel.Contains(m.Id)))
                {
                    var src = State.SourceOf(m);
                    total += src != null && src.Rows != null ? src.Rows.Value : 0;
                }
            }

            var pct = RunLogic.Pct(done, total);
            _rowsText.Text = RunLogic.N(done);
            _ofText.Text = "/ " + RunLogic.N(total) + " 행";
            _pctText.Text = Math.Floor(pct) + "%";
            _runIdText.Text = view != null && view.RunId != null ? view.RunId + " · MigrationAgent.exe PID " + view.Pid : (view != null && view.Stage != null ? view.Stage : "");
            var pill = Kit.Pill(RunLogic.PillKind(state), RunLogic.StateText(state, view != null && view.Dry), null);
            _statePill.Child = pill;

            var current = snap != null ? snap.Tasks.FirstOrDefault(t => t.Status == "run" || t.Status == "paused") : null;
            _currentText.Text = current != null
                ? "지금: " + current.Label + " (" + (snap.Tasks.IndexOf(current) + 1) + "/" + snap.Tasks.Count + ")"
                : snap == null ? SelectedSummary() : "";
            _bar.Set(pct, state == RunStates.Paused || state == RunStates.Pausing ? "paused" : state == RunStates.Done ? "done" : state == RunStates.Stopped || state == RunStates.Failed ? "stopped" : "");

            _stats["rate"].Text = snap != null && state == RunStates.Running ? RunLogic.Rate(snap.Rate) + " 행/초" : "—";
            _stats["elapsed"].Text = snap != null ? RunLogic.Duration(snap.Elapsed) : "—";
            _stats["eta"].Text = snap != null && snap.Totals != null && snap.Totals.EtaSeconds != null && state == RunStates.Running ? RunLogic.Duration(snap.Totals.EtaSeconds.Value) : state == RunStates.Done ? "00:00:00" : "—";
            _stats["commits"].Text = snap != null ? RunLogic.N(snap.Tasks.Sum(t => (long)t.Commits)) : "—";
            _stats["ins"].Text = snap != null ? RunLogic.N(snap.Tasks.Sum(t => t.Inserted)) : "—";
            _stats["upd"].Text = snap != null ? RunLogic.N(snap.Tasks.Sum(t => t.Updated)) : "—";
            var rej = snap != null ? snap.Tasks.Sum(t => t.Rejected) : 0;
            _stats["rej"].Text = snap != null ? RunLogic.N(rej) : "—";
            _stats["rej"].Foreground = rej > 0 ? Theme.Warning : null;
            if (rej <= 0)
            {
                _stats["rej"].SetResourceReference(TextBlock.ForegroundProperty, Theme.PrimaryText);
            }

            var st = State.Job.Strategy ?? new MigrationStrategy();
            var fetch = view != null && view.FetchSize > 0 ? view.FetchSize : st.FetchSize;
            var commit = view != null && view.CommitSize > 0 ? view.CommitSize : st.CommitSize;
            var workers = view != null && view.Workers > 0 ? view.Workers : st.Workers;
            var cardsModel = RunLogic.Pipeline(snap, fetch, commit, workers, ModeLabelOfCurrent(current));
            for (var i = 0; i < _stages.Count && i < cardsModel.Count; i++)
            {
                _stages[i].Metric.Text = cardsModel[i].Metric;
                _stages[i].Sub.Text = cardsModel[i].Sub;
                _stages[i].Bar.Set(cardsModel[i].Percent, cardsModel[i].Active ? "" : "");
                if (cardsModel[i].Active)
                {
                    _stages[i].Frame.SetResourceReference(Border.BorderBrushProperty, Theme.Accent);
                }
                else
                {
                    _stages[i].Frame.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
                }
            }

            DrawTasks(snap);
            AppendLog();
            if (force || (DateTime.Now - _lastCheckpointDraw).TotalMilliseconds > 500 || !RunLogic.IsActive(state))
            {
                DrawCheckpoints();
            }

            var controls = RunLogic.Controls(state, State.Job.Mappings != null && State.Job.Mappings.Count > 0);
            if (_startButton != null)
            {
                _startButton.IsEnabled = controls.CanStart;
                _pauseButton.IsEnabled = controls.CanPause;
                _resumeButton.IsEnabled = controls.CanResume;
                _stopButton.IsEnabled = controls.CanStop;
            }
        }

        private string ModeLabelOfCurrent(TaskSnapshot task)
        {
            if (task == null)
            {
                return "";
            }

            var m = State.Job.Mappings != null ? State.Job.Mappings.FirstOrDefault(x => x.Id == task.Key) : null;
            return m != null ? WriteModes.Of(m.Mode).Label : "";
        }

        private string SelectedSummary()
        {
            var n = Selection().Count;
            if (State.Job.Mappings == null || State.Job.Mappings.Count == 0)
            {
                return "고른 작업이 없습니다";
            }

            return n == 0 ? "고른 작업이 없습니다" : "시작하면 " + n + "개 작업을 차례로 실행합니다";
        }

        private void DrawTasks(RunSnapshot snap)
        {
            _taskHost.Children.Clear();
            var grid = new RowGrid(
                new RowGridColumn { Header = "#", Width = new GridLength(30) },
                new RowGridColumn { Header = "작업", Width = new GridLength(2.2, GridUnitType.Star) },
                new RowGridColumn { Header = "상태", Width = new GridLength(92) },
                new RowGridColumn { Header = "진행", Width = new GridLength(1.1, GridUnitType.Star) },
                new RowGridColumn { Header = "행", Width = new GridLength(118), HeaderAlign = TextAlignment.Right },
                new RowGridColumn { Header = "Inserted", Width = new GridLength(78), HeaderAlign = TextAlignment.Right },
                new RowGridColumn { Header = "Updated", Width = new GridLength(70), HeaderAlign = TextAlignment.Right },
                new RowGridColumn { Header = "Rejected", Width = new GridLength(66), HeaderAlign = TextAlignment.Right },
                new RowGridColumn { Header = "체크포인트", Width = new GridLength(1.3, GridUnitType.Star) });
            var rows = new List<TaskRowModel>();
            if (snap != null)
            {
                for (var i = 0; i < snap.Tasks.Count; i++)
                {
                    long baseRows;
                    View.BaseRows.TryGetValue(snap.Tasks[i].Key, out baseRows);
                    rows.Add(RunLogic.TaskRow(i + 1, snap.Tasks[i], baseRows));
                }
            }
            else
            {
                var sel = Selection();
                var i = 0;
                foreach (var m in Ordered().Where(x => sel.Contains(x.Id)))
                {
                    var src = State.SourceOf(m);
                    var tgt = State.TargetTable(m.Target);
                    rows.Add(new TaskRowModel
                    {
                        Index = ++i,
                        Label = m.Label,
                        StatusText = "대기",
                        StatusKind = "wait",
                        Percent = 0,
                        RowsText = "0 / " + RunLogic.Short(src != null && src.Rows != null ? src.Rows.Value : 0),
                        Inserted = "—",
                        Updated = m.Mode == "MERGE" && tgt != null && tgt.Rows != null ? "~" + RunLogic.N(Math.Min(tgt.Rows.Value, src != null && src.Rows != null ? src.Rows.Value : 0)) : "—",
                        Rejected = "—",
                        Checkpoint = string.IsNullOrEmpty(m.CheckpointColumn) ? "없음" : "—"
                    });
                }
            }

            foreach (var r in rows)
            {
                var badge = r.StatusKind == "run" ? new StackPanel { Orientation = Orientation.Horizontal } : null;
                FrameworkElement status;
                if (badge != null)
                {
                    badge.Children.Add(Kit.Spinner());
                    var t = Theme.Text(" " + r.StatusText);
                    t.FontSize = 11.5;
                    badge.Children.Add(t);
                    status = badge;
                }
                else
                {
                    var level = r.StatusKind == "done" ? "PASS" : r.StatusKind == "failed" ? "ERROR" : r.StatusKind == "stopped" || r.StatusKind == "paused" ? "WARN" : "SKIP";
                    status = Kit.LevelBadge(level, r.StatusText);
                }

                var label = Theme.Text(r.Label);
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                label.ToolTip = r.Label;
                label.VerticalAlignment = VerticalAlignment.Center;
                label.FontSize = 12;
                var progress = new ProgressBarView(6);
                progress.Set(r.Percent, r.StatusKind == "done" ? "done" : r.StatusKind == "failed" || r.StatusKind == "stopped" ? "stopped" : r.StatusKind == "paused" ? "paused" : "");
                var progressCell = new DockPanel();
                var pctText = Theme.Secondary(Math.Floor(r.Percent) + "%");
                pctText.Width = 36;
                pctText.TextAlignment = TextAlignment.Right;
                DockPanel.SetDock(pctText, Dock.Right);
                progressCell.Children.Add(pctText);
                progress.Root.Margin = new Thickness(0, 0, 6, 0);
                progress.Root.VerticalAlignment = VerticalAlignment.Center;
                progressCell.Children.Add(progress.Root);
                var cp = Theme.Secondary(r.Checkpoint);
                cp.FontFamily = Theme.Mono;
                cp.FontSize = 11;
                cp.TextTrimming = TextTrimming.CharacterEllipsis;
                grid.AddRow(new FrameworkElement[]
                {
                    Right(Theme.Secondary(r.Index.ToString()), false), label, status, progressCell,
                    Right(Theme.Text(r.RowsText), false), Right(Theme.Text(r.Inserted), false), Right(Theme.Text(r.Updated), false),
                    Right(r.HasRejects ? Warn(r.Rejected) : Theme.Text(r.Rejected), false), cp
                }, r, null, null);
            }

            _taskHost.Children.Add(grid.Root);
        }

        private static TextBlock Right(TextBlock t, bool bold)
        {
            t.TextAlignment = TextAlignment.Right;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.FontSize = 12;
            return t;
        }

        private static TextBlock Warn(string text)
        {
            var t = Theme.Text(text);
            t.Foreground = Theme.Warning;
            return t;
        }

        private void AppendLog()
        {
            var view = View;
            if (view == null || _logPanel == null)
            {
                if (_logPanel != null && _logPanel.Children.Count == 0)
                {
                    var empty = Theme.Secondary("실행하면 [START] · [INFO] Committed … · [DONE] Inserted / Updated / Rejected 로그가 여기에 나옵니다.");
                    empty.TextWrapping = TextWrapping.Wrap;
                    empty.Margin = new Thickness(10, 8, 10, 8);
                    _logPanel.Children.Add(empty);
                }

                return;
            }

            var filter = State.Ui.LogFilter ?? "all";
            var atBottom = _logScroll.VerticalOffset + _logScroll.ViewportHeight >= _logScroll.ExtentHeight - 30;
            if (_logShown == 0)
            {
                _logPanel.Children.Clear();
            }

            var from = Math.Max(_logShown, _logCleared);
            for (var i = from; i < view.Log.Count; i++)
            {
                var e = view.Log[i];
                if (RunLogic.LogVisible(e.Tag, filter))
                {
                    _logPanel.Children.Add(LogLine(e));
                }
            }

            while (_logPanel.Children.Count > RunPresenter.MaxLogLines)
            {
                _logPanel.Children.RemoveAt(0);
            }

            _logShown = view.Log.Count;
            if (_logPanel.Children.Count == 0)
            {
                var empty = Theme.Secondary("실행하면 [START] · [INFO] Committed … · [DONE] Inserted / Updated / Rejected 로그가 여기에 나옵니다.");
                empty.TextWrapping = TextWrapping.Wrap;
                empty.Margin = new Thickness(10, 8, 10, 8);
                _logPanel.Children.Add(empty);
            }

            if (atBottom)
            {
                _logScroll.ScrollToEnd();
            }
        }

        private static UIElement LogLine(LogEntry e)
        {
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = Theme.Mono, FontSize = 12, Margin = new Thickness(10, 2, 10, 2) };
            var ts = new Run(e.At.ToString("HH:mm:ss") + " ");
            ts.SetResourceReference(TextElement.ForegroundProperty, Theme.DisabledText);
            t.Inlines.Add(ts);
            var tag = new Run("[" + e.Tag + "] ") { FontWeight = FontWeights.SemiBold };
            switch (e.Tag)
            {
                case "START":
                case "RESUME":
                    tag.SetResourceReference(TextElement.ForegroundProperty, Theme.Accent);
                    break;
                case "WARN":
                case "PAUSE":
                    tag.Foreground = Theme.Warning;
                    break;
                case "ERROR":
                case "STOP":
                    tag.Foreground = Theme.Danger;
                    break;
                case "DONE":
                    tag.Foreground = Theme.Success;
                    break;
                case "DRY":
                    tag.Foreground = Theme.SqlTag;
                    break;
                default:
                    tag.SetResourceReference(TextElement.ForegroundProperty, Theme.SecondaryText);
                    break;
            }

            t.Inlines.Add(tag);
            var lines = (e.Text ?? "").Split('\n');
            t.Inlines.Add(new Run(lines[0]));
            for (var i = 1; i < lines.Length; i++)
            {
                t.Inlines.Add(new LineBreak());
                t.Inlines.Add(new Run("           " + lines[i]));
            }

            return t;
        }

        // ---------- 체크포인트 카드 ----------

        private void DrawCheckpoints()
        {
            _lastCheckpointDraw = DateTime.Now;
            if (_cpHost == null)
            {
                return;
            }

            var cps = State.Job.Checkpoints ?? new Dictionary<string, CheckpointInfo>();
            var body = new StackPanel();
            if (cps.Count == 0)
            {
                body.Children.Add(Kit.EmptyState(Icons.Checklist, "체크포인트 없음", "체크포인트 컬럼이 있는 작업은 커밋마다 마지막 키를 남깁니다."));
            }

            foreach (var pair in cps.ToList())
            {
                var cp = pair.Value;
                var key = pair.Key;
                var mapping = State.Mapping(key);
                var item = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
                var head = new DockPanel();
                var del = Kit.IconButton(Icons.Delete, "체크포인트 지우기(다음 실행은 처음부터)");
                del.IsEnabled = !RunLogic.IsActive(CurrentState);
                del.Click += (s, e) =>
                {
                    if (Dialogs.ConfirmDanger(_host.Owner, "체크포인트 지우기", (mapping != null ? mapping.Label : key) + "의 체크포인트를 지울까요? 다음 실행은 처음부터 합니다."))
                    {
                        State.Job.Checkpoints.Remove(key);
                        State.MarkChanged();
                        DrawCheckpoints();
                    }
                };
                DockPanel.SetDock(del, Dock.Right);
                head.Children.Add(del);
                var pill = Kit.Pill(cp.Status == "done" ? "ok" : cp.Status == "running" ? "run" : "warn", RunLogic.CheckpointStatusText(cp.Status), null);
                pill.Margin = new Thickness(0, 0, 6, 0);
                DockPanel.SetDock(pill, Dock.Right);
                head.Children.Add(pill);
                var name = Theme.Text(mapping != null ? mapping.Label : key);
                name.FontWeight = FontWeights.SemiBold;
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                head.Children.Add(name);
                item.Children.Add(head);
                var kv = Kit.KeyValue(new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("Last Successful", cp.Column + " = " + cp.Value),
                    new KeyValuePair<string, string>("진행", RunLogic.N(cp.Rows) + " / " + RunLogic.N(cp.Total) + " (" + Math.Floor(RunLogic.Pct(cp.Rows, cp.Total)) + "%)"),
                    new KeyValuePair<string, string>("시각", (cp.At ?? "") + " · " + (cp.RunId ?? ""))
                });
                kv.Margin = new Thickness(0, 6, 0, 6);
                item.Children.Add(kv);
                if (cp.Status != "done")
                {
                    var code = new TextBox { Text = RunLogic.CheckpointWhere(cp.Column, cp.Value), IsReadOnly = true, FontFamily = Theme.Mono, FontSize = 11.5, BorderThickness = new Thickness(1) };
                    item.Children.Add(code);
                }
                else
                {
                    var note = Theme.Secondary("증분 이관을 하면 " + cp.Column + " > " + cp.Value + "부터 읽습니다");
                    note.FontSize = 11.5;
                    note.TextWrapping = TextWrapping.Wrap;
                    item.Children.Add(note);
                }

                body.Children.Add(item);
            }

            var cpScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = body };
            _cpHost.Child = Kit.Card(Icons.Checklist + "  체크포인트 · 커밋마다 저장", null, cpScroll, null);
        }

        // ================= 시작 흐름 =================

        /// <summary>이관 시작(F5·버튼). 일시정지 중이면 이어서.</summary>
        public void StartRun(string mode)
        {
            var state = CurrentState;
            if (state == RunStates.Paused)
            {
                _presenter.Resume();
                return;
            }

            if (RunLogic.IsActive(state))
            {
                UiTrace.Write("run", "시작 무시 — 이미 " + state);
                return;
            }

            mode = string.IsNullOrEmpty(mode) ? State.Ui.RunMode ?? "EXECUTE" : mode;
            var selection = Selection();
            var sel = (State.Job.Mappings ?? new List<Mapping>()).Where(m => selection.Contains(m.Id)).ToList();
            var inputs = new StartInputs
            {
                HasMetadata = _host.Operations.HasMetadata(),
                SelectedCount = sel.Count,
                Mode = mode,
                HasValidation = State.Pre != null && !State.Pre.Running,
                ValidationRunning = State.Pre != null && State.Pre.Running,
                ValidationStale = State.Pre != null && State.Pre.JobVersion != State.JobVersion,
                Gate = State.Pre != null ? ValidationGate.Evaluate(State.Pre.Items, selection) : null,
                SelectedWithCheckpoint = sel.Count(m => State.Job.Checkpoints != null && State.Job.Checkpoints.ContainsKey(m.Id) && State.Job.Checkpoints[m.Id].Status != "done")
            };
            var target = State.ProfileForRole(Roles.Target);
            var schema = State.Job.Target != null ? State.Job.Target.Schema : "";
            var destructive = sel.Where(m => RunLogic.IsDestructive(m.Mode))
                .Select(m => WriteModes.Of(m.Mode).Label + "  " + schema + "." + m.Target).ToList();
            inputs.DestructiveLabels = destructive;

            for (var guard = 0; guard < 8; guard++)
            {
                var gate = RunLogic.NextStartGate(inputs);
                UiTrace.Write("run", "시작 관문 " + gate + " · 모드 " + mode + " · 선택 " + sel.Count + " · 검증 " + (inputs.HasValidation ? (inputs.ValidationStale ? "있음(바뀜)" : "있음") : "없음"));
                switch (gate)
                {
                    case StartGate.NeedMetadata:
                        Kit.Toast(_host.Owner, "메타데이터를 먼저 불러오세요", "warn");
                        return;
                    case StartGate.NoSelection:
                        Kit.Toast(_host.Owner, "실행할 작업을 고르세요", "warn");
                        return;
                    case StartGate.NoCheckpointToResume:
                        Kit.Toast(_host.Owner, "재개할 체크포인트가 없습니다 — 이관 실행으로 시작하세요", "warn");
                        return;
                    case StartGate.AskValidateFirst:
                        var choice = RunDialogs.AskValidateFirst(_host.Owner);
                        if (choice == RunDialogs.ValidateFirstChoice.Validate)
                        {
                            _host.RunValidation();
                            return;
                        }

                        if (choice == RunDialogs.ValidateFirstChoice.Cancel)
                        {
                            return;
                        }

                        inputs.SkipValidateAsk = true;
                        continue;
                    case StartGate.Blocked:
                        if (RunDialogs.ShowBlocked(_host.Owner, inputs.Gate))
                        {
                            _host.GoToStep(3);
                        }

                        return;
                    case StartGate.ConfirmStale:
                        if (!RunDialogs.ConfirmStale(_host.Owner))
                        {
                            return;
                        }

                        inputs.StaleAccepted = true;
                        continue;
                    case StartGate.ConfirmDestructive:
                        if (!RunDialogs.ConfirmDestructive(_host.Owner, target, schema, destructive))
                        {
                            return;
                        }

                        inputs.DestructiveAccepted = true;
                        continue;
                    default:
                        Launch(mode, selection);
                        return;
                }
            }
        }

        private async void Launch(string mode, ISet<string> selection)
        {
            var st = State.Job.Strategy ?? new MigrationStrategy();
            var view = _presenter.Begin(mode, st.FetchSize, st.CommitSize, st.Workers);
            foreach (var pair in State.Job.Checkpoints ?? new Dictionary<string, CheckpointInfo>())
            {
                if (mode == "RESUME" && pair.Value != null && pair.Value.Status != "done")
                {
                    view.BaseRows[pair.Key] = pair.Value.Rows;
                }
            }

            if (View != null && mode == "RESUME" && View.Stage == null)
            {
                View.Stage = null;
            }

            _startCts = new CancellationTokenSource();
            var progress = new Progress<string>(text =>
            {
                if (View != null && View.State == RunStates.Starting)
                {
                    View.Stage = text;
                    UpdateLive(false);
                }
            });
            try
            {
                var service = _serviceFactory();
                var client = await service.StartAsync(new RunRequest
                {
                    Job = State.Job,
                    SourceMeta = State.SourceMeta(),
                    TargetMeta = State.TargetMeta(),
                    Selected = selection,
                    Mode = mode
                }, progress, _startCts.Token).ConfigureAwait(true);
                _presenter.Attach(client, mode, false);
                UiTrace.Write("run", "시작 " + client.RunId + " (pid " + client.Pid + ", " + mode + ")");
                Kit.Toast(_host.Owner, "실행 " + client.RunId + " 시작", "ok");
            }
            catch (OperationCanceledException)
            {
                UiTrace.Write("run", "시작 취소");
                _presenter.StartFailed("시작을 취소했습니다.");
            }
            catch (Exception ex)
            {
                UiTrace.Write("run", "시작 실패: " + ex);
                _presenter.StartFailed(ex.Message);
            }
        }

        public void CancelStart()
        {
            if (_startCts != null)
            {
                _startCts.Cancel();
            }
        }
    }
}
