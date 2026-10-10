using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using MigrationStudio.Core.Mapping;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Sql;
using MigrationStudio.Logic;
using MigrationStudio.Services;

namespace MigrationStudio.Ui.Pages
{
    internal sealed class ColumnsPage : PageFrame
    {
        private readonly IMappingUiHost _host;
        private readonly StackPanel _body = new StackPanel();

        public ColumnsPage(IMappingUiHost host)
        {
            _host = host;
            SetStep(2, Labels.StepTitles[2], "원본(또는 SQL 결과) 열을 대상 열에 연결하고 변환식·NULL 처리를 정합니다.");
            Refresh();
        }

        public void RefreshPartial(ChangeScope scope)
        {
            Refresh();
        }

        public void Refresh()
        {
            _body.Children.Clear();
            SetBody(_body);
            var mappings = _host.State.Job.Mappings;
            if (mappings == null || mappings.Count == 0)
            {
                _body.Children.Add(EmptyWithAction(Icons.List, "매핑이 없습니다", "먼저 원본(테이블 또는 SQL)과 대상 테이블을 이어 주세요.", "테이블 매핑으로", () => _host.GoToStep(1)));
                return;
            }

            if (!_host.Operations.HasMetadata())
            {
                _body.Children.Add(EmptyWithAction(Icons.Warn, "메타데이터 없음", "접속 화면에서 메타데이터를 불러오세요.", "접속으로", () => _host.GoToStep(0)));
                return;
            }

            var m = ResolveSelectedMapping();
            if (m == null)
            {
                m = mappings[0];
                _host.State.Ui.SelMapping = m.Id;
            }

            var src = _host.State.SourceOf(m);
            var tgt = _host.State.TargetTable(m.Target);
            if (m.IsSql && (src == null || src.Columns == null || src.Columns.Count == 0))
            {
                _body.Children.Add(EmptyWithAction(Icons.Code, "SQL 원본의 결과 열을 알 수 없습니다",
                    (src != null ? src.Comment : "SQL 검증 필요") + " — SQL 원본 편집기에서 SELECT 문을 고치고 검증하세요.",
                    "SQL 원본 편집기", () => _host.OpenSqlEditor(m.Id)));
                return;
            }

            if (src == null || tgt == null)
            {
                _body.Children.Add(EmptyWithAction(Icons.Warn, "원본 또는 대상을 찾을 수 없습니다", "접속·매핑 설정을 확인하세요.", "접속으로", () => _host.GoToStep(0)));
                return;
            }

            var pickerOpts = mappings.Select(x =>
            {
                var s = _host.State.SourceOf(x);
                var t = _host.State.TargetTable(x.Target);
                return new Kit.ComboOption { Value = x.Id, Label = ColumnsLogic.MappingPickerLabel(x, s, t) };
            }).ToList();
            var picker = Kit.CellComboBox(pickerOpts, m.Id, id =>
            {
                _host.State.Ui.SelMapping = id;
                _host.State.Ui.SelColumn = null;
                _host.State.MarkUiChanged();
                Refresh();
            });
            picker.MinWidth = 300;
            SetHeaderActions(picker);

            _body.Children.Add(BuildSettingsCard(m, src, tgt));
            _body.Children.Add(BuildColumnWorkspace(m, src, tgt));
            _body.Children.Add(BuildSqlCard(m));
            SetFooterHint(m.Label);
        }

        private Mapping ResolveSelectedMapping()
        {
            var id = _host.State.Ui.SelMapping;
            return string.IsNullOrEmpty(id) ? null : _host.State.Mapping(id);
        }

        private UIElement EmptyWithAction(string icon, string title, string text, string btnLabel, Action action)
        {
            var p = Kit.EmptyState(icon, title, text);
            var b = Kit.PrimaryButton(btnLabel, null);
            b.Click += (s, e) => action();
            p.Children.Add(b);
            return p;
        }

        private UIElement BuildSettingsCard(Mapping m, TableMetadata src, TableMetadata tgt)
        {
            var modeOpts = WriteModes.All.Select(x => new Kit.ComboOption { Value = x.Value, Label = x.Label }).ToList();
            var mode = Kit.CellComboBox(modeOpts, m.Mode, v => { m.Mode = v; _host.State.MarkChanged(); });
            var cpOpts = ColumnsLogic.CheckpointCandidates(src).Select(c => new Kit.ComboOption { Value = c.StartsWith("(") ? "" : c, Label = c }).ToList();
            var cp = Kit.CellComboBox(cpOpts, m.CheckpointColumn ?? "", v => { m.CheckpointColumn = string.IsNullOrEmpty(v) ? null : v; _host.State.MarkChanged(); });
            var where = new TextBox { FontFamily = Theme.Mono, Text = m.Where ?? "", AcceptsReturn = false };
            where.LostFocus += (s, e) => { m.Where = where.Text; _host.State.MarkChanged(); };
            var keys = new WrapPanel();
            var needsKey = WriteModes.Of(m.Mode).NeedsKey;
            if (!needsKey)
            {
                keys.Children.Add(Theme.Secondary("이 방식에는 필요 없음"));
            }
            else if (m.MergeKey == null || m.MergeKey.Count == 0)
            {
                var warn = new TextBlock { Text = "표의 ‘키’에서 고르세요", Foreground = Theme.Danger };
                keys.Children.Add(warn);
            }
            else
            {
                foreach (var key in m.MergeKey)
                {
                    var chip = Kit.Tag(key + "  ×", "pk");
                    chip.Margin = new Thickness(0, 2, 4, 2);
                    keys.Children.Add(chip);
                }
            }

            var grid = Kit.FormGrid(4,
                Kit.Field("이관 방식", mode, false, null),
                Kit.Field("병합 키", keys, false, null),
                Kit.Field("체크포인트", cp, false, null),
                Kit.Field("원본 조건 (WHERE)", where, false, "테이블/SQL 결과에 AND로 붙습니다"));
            var stack = new StackPanel();
            stack.Children.Add(grid);
            stack.Children.Add(BuildDeleteRow(m, tgt));
            var panel = new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = stack, Margin = new Thickness(0, 0, 0, 12) };
            panel.SetResourceReference(Border.BackgroundProperty, Theme.PanelBackground);
            panel.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            return panel;
        }

        /// <summary>
        /// 원본에서 지운 행 처리(증분·CDC에서만 씀). 따라가지 않음이 기본, "대상에 삭제 표시"는 표시 열·값을 고르고 실행 화면에서 승인해야 표시한다.
        /// 영구 삭제는 아직 없다(되돌릴 수 있는 표시만).
        /// </summary>
        private UIElement BuildDeleteRow(Mapping m, TableMetadata tgt)
        {
            var modes = new List<Kit.ComboOption>
            {
                new Kit.ComboOption { Value = DeleteModes.None, Label = DeleteModes.Label(DeleteModes.None) },
                new Kit.ComboOption { Value = DeleteModes.Mark, Label = DeleteModes.Label(DeleteModes.Mark) }
            };
            var mark = DeleteModes.IsMark(m.DeleteMode);
            var modeBox = Kit.CellComboBox(modes, mark ? DeleteModes.Mark : DeleteModes.None, v =>
            {
                if (ColumnsLogic.ChangeDeleteSetting(m, v, m.MarkColumn, m.MarkValue))
                {
                    _host.State.MarkChanged();
                    Refresh();
                }
            });
            var hint = "증분·CDC에서만. 원본에서 지운 행을 대상에 어떻게 반영할지 정합니다.";
            if (!mark)
            {
                var only = Kit.FormGrid(4, Kit.Field("원본 삭제", modeBox, false, hint + " 따라가지 않으면 대상에 남고, 실행 후 검증·동기화 화면에 차이로 보입니다."));
                only.Margin = new Thickness(0, 10, 0, 0);
                return only;
            }

            var candidates = ColumnsLogic.MarkColumnCandidates(m, tgt).Select(c => new Kit.ComboOption { Value = c, Label = c }).ToList();
            if (!string.IsNullOrEmpty(m.MarkColumn) && candidates.All(c => !string.Equals(c.Value, m.MarkColumn, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Insert(0, new Kit.ComboOption { Value = m.MarkColumn, Label = m.MarkColumn + " (쓸 수 없음 — 검증 참고)" });
            }

            var columnBox = Kit.CellComboBox(candidates, m.MarkColumn ?? "", v =>
            {
                if (ColumnsLogic.ChangeDeleteSetting(m, m.DeleteMode, v, m.MarkValue))
                {
                    _host.State.MarkChanged();
                    Refresh();
                }
            });
            var valueBox = new TextBox { FontFamily = Theme.Mono, Text = m.MarkValue ?? "" };
            valueBox.LostFocus += (s, e) =>
            {
                if (ColumnsLogic.ChangeDeleteSetting(m, m.DeleteMode, m.MarkColumn, valueBox.Text))
                {
                    _host.State.MarkChanged();
                    Refresh();
                }
            };
            var approval = new StackPanel();
            if (string.IsNullOrEmpty(m.DeleteApprovedAt))
            {
                var pending = new TextBlock { Text = "승인 전 — 대조만 하고 표시하지 않음", Foreground = Theme.Warning, TextWrapping = TextWrapping.Wrap };
                approval.Children.Add(pending);
            }
            else
            {
                approval.Children.Add(Theme.Secondary("승인 " + m.DeleteApprovedAt));
                approval.Children.Add(Kit.LinkButton("승인 취소", () =>
                {
                    m.DeleteApprovedAt = null;
                    _host.State.MarkChanged();
                    Refresh();
                }));
            }

            var row = Kit.FormGrid(4,
                Kit.Field("원본 삭제", modeBox, false, hint),
                Kit.Field("표시 열", columnBox, false, "대상의 NULL 허용 열(NULL = 살아 있음). 원본에 다시 나타나면 NULL로 되돌립니다."),
                Kit.Field("표시 값", valueBox, false, "예: Y, 1, SYSDATE(날짜 열)"),
                Kit.Field("승인", approval, false, "Dry Run에서 표시될 행을 본 뒤 실행 화면에서 승인합니다. 설정을 바꾸면 승인이 지워집니다."));
            row.Margin = new Thickness(0, 10, 0, 0);
            return row;
        }

        private UIElement BuildColumnWorkspace(Mapping m, TableMetadata src, TableMetadata tgt)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var table = BuildColumnGrid(m, src, tgt) as FrameworkElement;
            var inspector = BuildInspector(m, src, tgt) as FrameworkElement;
            table.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(inspector, 1);
            grid.Children.Add(table);
            grid.Children.Add(inspector);
            grid.SizeChanged += (s, e) =>
            {
                var stacked = e.NewSize.Width < 980;
                grid.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(340);
                Grid.SetColumn(inspector, stacked ? 0 : 1);
                Grid.SetRow(inspector, stacked ? 1 : 0);
                Grid.SetColumnSpan(inspector, stacked ? 2 : 1);
                table.Margin = stacked ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 12, 0);
            };
            return grid;
        }

        private UIElement BuildColumnGrid(Mapping m, TableMetadata src, TableMetadata tgt)
        {
            var st = MappingService.Status(m, src, tgt);
            var filter = _host.State.Ui.ColFilter ?? "all";
            var seg = Kit.Segmented(new[]
            {
                new Kit.SegmentOption { Value = "all", Label = "전체" },
                new Kit.SegmentOption { Value = "unmapped", Label = "매핑 안 됨" },
                new Kit.SegmentOption { Value = "issues", Label = "경고·오류" }
            }, filter, v => { _host.State.Ui.ColFilter = v; _host.State.MarkUiChanged(); Refresh(); });

            var tools = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            tools.Children.Add(seg);
            var autoBtn = Kit.Button("이름으로 자동 매핑", Icons.Magic);
            autoBtn.Click += (s, e) => AutoMap(m, src, tgt);
            tools.Children.Add(autoBtn);

            var grid = new RowGrid(
                new RowGridColumn { Header = "원본 컬럼", Width = new GridLength(1, GridUnitType.Star) },
                new RowGridColumn { Header = "", Width = new GridLength(22) },
                new RowGridColumn { Header = "대상 컬럼", Width = new GridLength(1.15, GridUnitType.Star) },
                new RowGridColumn { Header = "변환식 (Transform)", Width = new GridLength(1.2, GridUnitType.Star) },
                new RowGridColumn { Header = "NULL 처리", Width = new GridLength(112) },
                new RowGridColumn { Header = "키", Width = new GridLength(36) },
                new RowGridColumn { Header = "검사", Width = new GridLength(70) });

            var any = false;
            foreach (var r in st.Results)
            {
                if (!ColumnsLogic.PassesFilter(r, filter == "issues" ? "issues" : filter))
                {
                    continue;
                }

                any = true;
                AddColumnRow(grid, m, src, r);
            }

            var body = any ? (UIElement)grid.Root : Kit.EmptyState(Icons.Check, "해당하는 컬럼이 없습니다", "");
            return Kit.Card(Icons.List + "  컬럼 · " + st.Mapped + "/" + st.Total + " · 오류 " + st.Errors, tools, body, null);
        }

        private void AddColumnRow(RowGrid grid, Mapping m, TableMetadata src, ColumnResult r)
        {
            var cm = r.Mapping;
            var srcOpts = new List<Kit.ComboOption> { new Kit.ComboOption { Value = "", Label = "(매핑 안 함)" } };
            foreach (var c in src.Columns)
            {
                srcOpts.Add(new Kit.ComboOption { Value = c.Name, Label = c.Name });
            }

            var prev = cm.Source;
            var srcCombo = Kit.CellComboBox(srcOpts, cm.Source ?? "", v =>
            {
                var col = src.Columns.Find(c => c.Name == v);
                ColumnsLogic.ApplySourceColumnChange(cm, prev, v, col, r.Target);
                _host.State.MarkChanged();
                Refresh();
            });

            var srcPanel = new StackPanel();
            srcPanel.Children.Add(srcCombo);
            var srcMeta = src.FindColumn(cm.Source);
            var srcType = Theme.Secondary(srcMeta != null ? srcMeta.Type : "—");
            srcType.FontFamily = Theme.Mono;
            srcType.FontSize = 11;
            srcType.TextTrimming = TextTrimming.CharacterEllipsis;
            srcPanel.Children.Add(srcType);

            var tgtPanel = new StackPanel();
            var tgtName = Theme.Text(r.Target.Name);
            tgtName.FontFamily = Theme.Mono;
            tgtName.FontWeight = FontWeights.SemiBold;
            var tgtTop = new WrapPanel();
            tgtTop.Children.Add(tgtName);
            if (r.Target.PrimaryKey)
            {
                var pk = Kit.Tag("PK", "pk");
                pk.Margin = new Thickness(4, 0, 0, 0);
                tgtTop.Children.Add(pk);
            }
            if (!r.Target.Nullable)
            {
                var nn = Kit.Tag("NN", "ro");
                nn.Margin = new Thickness(4, 0, 0, 0);
                tgtTop.Children.Add(nn);
            }
            tgtPanel.Children.Add(tgtTop);
            var tgtType = Theme.Secondary(r.Target.Type + (string.IsNullOrWhiteSpace(r.Target.DefaultValue) ? "" : "  DEFAULT " + r.Target.DefaultValue));
            tgtType.FontFamily = Theme.Mono;
            tgtType.FontSize = 11;
            tgtType.TextTrimming = TextTrimming.CharacterEllipsis;
            tgtPanel.Children.Add(tgtType);
            var expr = string.IsNullOrWhiteSpace(cm.Expr) ? (cm.Source != null ? "그대로" : "—") : Regex.Replace(cm.Expr, "\\s+", " ").Trim();
            if (expr.Length > 80)
            {
                expr = expr.Substring(0, 77) + "...";
            }

            var nullOpts = NullRules.All.Select(n => new Kit.ComboOption { Value = n, Label = NullRules.Label(n) }).ToList();
            var nullCombo = Kit.CellComboBox(nullOpts, cm.NullRule ?? NullRules.Allow, v => { cm.NullRule = v; _host.State.MarkChanged(); });
            var badge = Kit.Pill(r.Check.Level == CheckLevels.Error ? "err" : r.Check.Level == CheckLevels.Warn ? "warn" : "ok", r.Check.Level, null);
            var key = new CheckBox { IsChecked = m.MergeKey != null && m.MergeKey.Contains(r.Target.Name), HorizontalAlignment = HorizontalAlignment.Center, IsEnabled = WriteModes.Of(m.Mode).NeedsKey };
            key.Click += (s, e) =>
            {
                if (key.IsChecked == true && !m.MergeKey.Contains(r.Target.Name))
                {
                    m.MergeKey.Add(r.Target.Name);
                }
                else if (key.IsChecked != true)
                {
                    m.MergeKey.Remove(r.Target.Name);
                }
                _host.State.MarkChanged();
            };
            var exprText = Theme.Secondary(expr);
            exprText.TextTrimming = TextTrimming.CharacterEllipsis;
            grid.AddRow(new FrameworkElement[] { srcPanel, Kit.Icon(Icons.Arrow), tgtPanel, exprText, nullCombo, key, badge }, r.Target.Name, () =>
            {
                _host.State.Ui.SelColumn = r.Target.Name;
                _host.State.MarkUiChanged();
                Refresh();
            }, null);
        }

        private UIElement BuildInspector(Mapping m, TableMetadata src, TableMetadata tgt)
        {
            var status = MappingService.Status(m, src, tgt);
            var selected = status.Results.FirstOrDefault(x => x.Target.Name == _host.State.Ui.SelColumn) ?? status.Results.FirstOrDefault();
            if (selected == null)
            {
                return Kit.Card(Icons.Check + "  검사기", null, Kit.EmptyState(Icons.Check, "검사할 컬럼이 없습니다", ""), null);
            }

            var cm = selected.Mapping;
            var body = new StackPanel();
            var target = selected.Target;
            var source = src.FindColumn(cm.Source);
            var title = target.Name + " ← " + (cm.Source ?? "매핑 안 됨");
            var sub = Theme.Secondary(target.Type + (target.Nullable ? "" : " · NOT NULL") + (string.IsNullOrEmpty(target.Comment) ? "" : " · " + target.Comment));
            sub.FontSize = 11.5;
            body.Children.Add(sub);
            body.Children.Add(Kit.SectionLabel("변환식 (Transform Expression)"));
            var expression = new TextBox { Text = cm.Expr ?? "", FontFamily = Theme.Mono, AcceptsReturn = true, MinHeight = 82, TextWrapping = TextWrapping.Wrap };
            expression.LostFocus += (s, e) => { cm.Expr = expression.Text; _host.State.MarkChanged(); Refresh(); };
            body.Children.Add(expression);
            var snippets = new WrapPanel { Margin = new Thickness(0, 6, 0, 8) };
            foreach (var name in ColumnsLogic.SnippetNames)
            {
                var button = Kit.GhostButton(name, null);
                button.Padding = new Thickness(5, 1, 5, 1);
                button.Margin = new Thickness(0, 0, 4, 4);
                button.Click += (s, e) => expression.Text = ColumnsLogic.WrapSnippet(name, string.IsNullOrWhiteSpace(expression.Text) ? cm.Source : expression.Text);
                snippets.Children.Add(button);
            }
            body.Children.Add(snippets);
            body.Children.Add(Kit.SectionLabel("원본 컬럼"));
            var sourceChips = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var column in src.Columns)
            {
                var button = Kit.GhostButton(column.Name, null);
                button.FontFamily = Theme.Mono;
                button.ToolTip = column.Type;
                button.Padding = new Thickness(5, 1, 5, 1);
                button.Margin = new Thickness(0, 0, 4, 4);
                button.Click += (s, e) => expression.SelectedText = column.Name;
                sourceChips.Children.Add(button);
            }
            body.Children.Add(sourceChips);
            body.Children.Add(Kit.SectionLabel("NULL 처리"));
            var nullOpts = NullRules.All.Select(n => new Kit.ComboOption { Value = n, Label = NullRules.Label(n) }).ToList();
            body.Children.Add(Kit.CellComboBox(nullOpts, cm.NullRule ?? NullRules.Allow, v => { cm.NullRule = v; _host.State.MarkChanged(); }));
            if (!string.Equals(cm.NullRule, NullRules.Allow, StringComparison.Ordinal))
            {
                var def = Kit.CellTextBox(cm.DefaultValue, v => { cm.DefaultValue = v; _host.State.MarkChanged(); }, true);
                def.Margin = new Thickness(0, 4, 0, 0);
                body.Children.Add(def);
            }
            body.Children.Add(Kit.SectionLabel("검사"));
            foreach (var message in selected.Check.Messages)
            {
                var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
                row.Children.Add(Kit.Pill(message.Level == CheckLevels.Error ? "err" : message.Level == CheckLevels.Warn ? "warn" : "ok", message.Level, null));
                var text = Theme.Secondary(message.Message);
                text.Margin = new Thickness(6, 2, 0, 0);
                text.TextWrapping = TextWrapping.Wrap;
                row.Children.Add(text);
                body.Children.Add(row);
            }
            if (selected.Check.Messages.Count == 0)
            {
                body.Children.Add(Kit.Pill("ok", "형식 호환", Icons.Check));
            }
            body.Children.Add(Kit.SectionLabel("샘플 미리보기 · 6행"));
            for (var i = 0; i < 6; i++)
            {
                var sample = Theme.Secondary((source != null ? source.Name : "NULL") + " 값 " + (i + 1) + "  →  " + (cm.Source ?? "NULL"));
                sample.FontFamily = Theme.Mono;
                sample.FontSize = 11;
                sample.Margin = new Thickness(0, 1, 0, 1);
                body.Children.Add(sample);
            }
            var scroll = new ScrollViewer { Content = body, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var card = Kit.Card(Icons.Check + "  " + title, null, scroll, null);
            card.BorderThickness = new Thickness(3, 1, 1, 1);
            card.SetResourceReference(Border.BorderBrushProperty, Theme.Accent);
            return card;
        }

        private void AutoMap(Mapping m, TableMetadata src, TableMetadata tgt)
        {
            var mapped = MappingService.AutoMapColumns(src.Columns, tgt.Columns);
            foreach (var item in mapped)
            {
                var existing = m.FindColumn(item.Mapping.Target);
                if (existing != null && string.IsNullOrEmpty(existing.Source))
                {
                    existing.Source = item.Mapping.Source;
                    existing.Expr = item.Mapping.Expr;
                }
            }

            _host.State.MarkChanged();
            Kit.Toast(_host.Owner, "자동 매핑: " + mapped.Count + "개 컬럼", "ok");
            Refresh();
        }

        private UIElement BuildSqlCard(Mapping m)
        {
            var schema = _host.State.Job.Source != null ? _host.State.Job.Source.Schema : "";
            var src = _host.State.SourceOf(m);
            var tgt = _host.State.TargetTable(m.Target);
            var strat = _host.State.Job.Strategy;
            var opts = new SourceSelectOptions
            {
                FetchSize = m.FetchSize ?? (strat != null ? strat.FetchSize : 5000),
                Workers = strat != null ? strat.Workers : 1
            };
            var select = SqlGenerator.BuildSourceSelect(m, schema, src, tgt, opts);
            var cols = m.Columns != null ? m.Columns.Select(c => c.Target).ToList() : new List<string>();
            var tgtSchema = _host.State.Job.Target != null ? _host.State.Job.Target.Schema : "";
            var errTable = strat != null ? strat.ErrorTable : "ERR$_";
            var write = SqlGenerator.BuildWriteSql(tgtSchema, m.Target, cols, m.Mode, m.MergeKey, errTable);
            var tab = _host.State.Ui.ColSqlTab ?? "select";
            var box = new TextBox
            {
                FontFamily = Theme.Mono,
                FontSize = 12,
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 320,
                Text = tab == "select" ? select : write
            };
            var tabs = Kit.Segmented(new[]
            {
                new Kit.SegmentOption { Value = "select", Label = "원본 SELECT" },
                new Kit.SegmentOption { Value = "write", Label = "대상 쓰기 문" }
            }, tab, v =>
            {
                _host.State.Ui.ColSqlTab = v;
                box.Text = v == "select" ? select : write;
            });
            var copy = Kit.GhostButton("복사", Icons.Copy);
            copy.Click += (s, e) => DialogKit.TryCopy(box.Text);
            var tools = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            tools.Children.Add(tabs);
            tools.Children.Add(copy);
            return Kit.Card(Icons.Code + "  생성 SQL", tools, box, Theme.Secondary("엔진이 실제로 실행하는 문장"));
        }
    }
}
