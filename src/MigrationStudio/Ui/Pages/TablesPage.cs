using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MigrationStudio.Core.Mapping;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Text;
using MigrationStudio.Logic;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Services;
using MigrationStudio.Ui.Modals;

namespace MigrationStudio.Ui.Pages
{
    internal sealed class TablesPage : PageFrame
    {
        private readonly IMappingUiHost _host;
        private readonly StackPanel _bodyPanel = new StackPanel();

        public TablesPage(IMappingUiHost host)
        {
            _host = host;
            SetStep(1, Labels.StepTitles[1], "원본 테이블·SQL 원본과 대상 테이블을 연결합니다.");
            RebuildHeaderActions();
            Refresh();
        }

        public void RefreshPartial(ChangeScope scope)
        {
            Refresh();
        }

        public void Refresh()
        {
            RebuildHeaderActions();
            _bodyPanel.Children.Clear();
            SetBody(_bodyPanel);

            if (!_host.Operations.HasMetadata())
            {
                var empty = Kit.EmptyState(Icons.Table, "메타데이터 없음", "접속 화면에서 원본·대상 메타데이터를 불러오세요.");
                var go = Kit.PrimaryButton("접속으로", null);
                go.Click += (s, e) => _host.GoToStep(0);
                empty.Children.Add(go);
                _bodyPanel.Children.Add(empty);
                SetFooterHint("");
                return;
            }

            var job = _host.State.Job;
            var mappings = job.Mappings ?? new List<Mapping>();
            var sqlCount = mappings.Count(m => m.IsSql);
            var usedRows = TablesLogic.UsedSourceRows(mappings, _host.State.SourceOf);

            var tools = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            var addBtn = Kit.Button("매핑 추가", Icons.Add);
            addBtn.Click += (s, e) => ShowAddDialog(null, false);
            var sqlBtn = Kit.GhostButton("SQL 원본 추가", Icons.Code);
            sqlBtn.Click += (s, e) => ShowAddDialog(null, true);
            var delBtn = Kit.GhostButton("삭제", Icons.Delete);
            delBtn.Click += (s, e) => DeleteSelected();
            var importBtn = Kit.GhostButton("JSON 가져오기", Icons.ImportFile);
            importBtn.Click += (s, e) => _host.Operations.ImportTemplate();
            var exportBtn = Kit.GhostButton("JSON 내보내기", Icons.ExportFile);
            exportBtn.Click += (s, e) => _host.ExportTemplate();
            tools.Children.Add(addBtn);
            tools.Children.Add(sqlBtn);
            tools.Children.Add(delBtn);
            tools.Children.Add(importBtn);
            tools.Children.Add(exportBtn);
            var filterText = _host.State.Ui.MappingFilter ?? "";
            tools.Children.Add(Kit.SearchBox("원본·대상 이름 찾기", filterText, v =>
            {
                _host.State.Ui.MappingFilter = v;
                _host.State.MarkUiChanged();
            }, 190));

            var subtitle = mappings.Count + "개(SQL 원본 " + sqlCount + ") · 사용 중 원본 " + Format.Number(usedRows) + "행";
            var cardBody = new StackPanel();
            if (mappings.Count == 0)
            {
                var empty = Kit.EmptyState(Icons.Swap, "매핑이 없습니다", "이름이 비슷한 테이블을 자동으로 맞추거나, 테이블 또는 SQL 원본을 직접 추가하세요.");
                var b1 = Kit.PrimaryButton("이름으로 자동 매칭", Icons.Magic);
                b1.Click += (s, e) => _host.OpenAutoMatch();
                empty.Children.Add(b1);
                cardBody.Children.Add(empty);
            }
            else
            {
                cardBody.Children.Add(BuildMappingGrid(mappings, filterText));
            }

            var gridCard = Kit.Card(Icons.Swap + "  테이블 매핑 · " + subtitle, tools, cardBody,
                Theme.Secondary("대상을 바꾸면 컬럼을 다시 자동 매핑합니다 · SQL 원본은 편집기에서 검증하세요"));

            var browse = BuildBrowseCard();
            gridCard.Margin = new Thickness(0, 0, 0, 12);
            _bodyPanel.Children.Add(gridCard);
            _bodyPanel.Children.Add(browse);

            var sel = _host.State.Ui.SelMapping != null ? _host.State.Mapping(_host.State.Ui.SelMapping) : null;
            SetFooterHint(sel != null ? "고른 매핑: " + sel.Label : "");
        }

        private void RebuildHeaderActions()
        {
            var btn = Kit.Button("이름으로 자동 매칭", Icons.Magic);
            btn.Click += (s, e) => _host.OpenAutoMatch();
            SetHeaderActions(btn);
        }

        private UIElement BuildMappingGrid(IList<Mapping> mappings, string filter)
        {
            var grid = new RowGrid(
                new RowGridColumn { Header = "사용", Width = new GridLength(32) },
                new RowGridColumn { Header = "원본 (테이블 · SQL)", Width = new GridLength(1, GridUnitType.Star) },
                new RowGridColumn { Header = "행 수", Width = new GridLength(70), HeaderAlign = TextAlignment.Right },
                new RowGridColumn { Header = "", Width = new GridLength(18) },
                new RowGridColumn { Header = "대상 테이블", Width = new GridLength(1.15, GridUnitType.Star) },
                new RowGridColumn { Header = "이관 방식", Width = new GridLength(154) },
                new RowGridColumn { Header = "병합 키", Width = new GridLength(106) },
                new RowGridColumn { Header = "컬럼 매핑", Width = new GridLength(80) },
                new RowGridColumn { Header = "상태", Width = new GridLength(62) });

            foreach (var m in mappings)
            {
                if (!TablesLogic.MatchesFilter(m, filter))
                {
                    continue;
                }

                AddMappingRow(grid, m);
            }

            if (string.Equals(_host.State.Ui.SelMapping, null, StringComparison.Ordinal) == false)
            {
                var sel = mappings.FirstOrDefault(x => x.Id == _host.State.Ui.SelMapping);
                if (sel != null)
                {
                    grid.SelectRow(sel);
                }
            }

            return grid.Root;
        }

        private void AddMappingRow(RowGrid grid, Mapping m)
        {
            var src = _host.State.SourceOf(m);
            var tgt = _host.State.TargetTable(m.Target);
            var st = MappingService.Status(m, src, tgt);
            var model = TablesLogic.RowModel(m, src, tgt, _host.State.ProfileForRole(Roles.Target), st);
            var use = new CheckBox { IsChecked = m.Use, VerticalAlignment = VerticalAlignment.Center };
            use.Click += (s, e) => e.Handled = true;
            use.Checked += (s, e) => { m.Use = true; _host.State.MarkChanged(); };
            use.Unchecked += (s, e) => { m.Use = false; _host.State.MarkChanged(); };
            if (!m.Use)
            {
                use.Opacity = 0.5;
            }

            var srcCell = new StackPanel();
            if (m.IsSql)
            {
                var srcRow = new StackPanel { Orientation = Orientation.Horizontal };
                srcRow.Children.Add(Kit.SqlTag("SQL"));
                var link = Kit.LinkButton(m.Source, () => _host.OpenSqlEditor(m.Id));
                link.FontFamily = Theme.Mono;
                link.FontSize = 11.5;
                link.FontWeight = FontWeights.SemiBold;
                link.TextTrimming = TextTrimming.CharacterEllipsis;
                link.ToolTip = m.Source;
                srcRow.Children.Add(link);
                srcCell.Children.Add(srcRow);
                var sub = Theme.Secondary(model.SourceSubline ?? "");
                sub.FontSize = 11;
                if (model.SourceBad)
                {
                    sub.Foreground = Theme.Danger;
                }

                srcCell.Children.Add(sub);
            }
            else
            {
                var name = Theme.Text(m.Source);
                name.FontFamily = Theme.Mono;
                name.FontSize = 11.5;
                name.FontWeight = FontWeights.SemiBold;
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                name.ToolTip = m.Source;
                srcCell.Children.Add(name);
            }

            var rows = Theme.Text(model.RowCountDisplay);
            rows.TextAlignment = TextAlignment.Right;
            var arrow = Kit.Icon(Icons.Arrow);
            arrow.HorizontalAlignment = HorizontalAlignment.Center;

            var tgtCombo = Kit.CellComboBox(TargetOptions(), m.Target, v => OnTargetChanged(m, v));
            var modeOpts = WriteModes.All.Select(x => new Kit.ComboOption { Value = x.Value, Label = x.Label }).ToList();
            var modeCombo = Kit.CellComboBox(modeOpts, m.Mode, v => OnModeChanged(m, v, tgt));
            var keyCombo = BuildKeyCombo(m, tgt);
            var colCell = new StackPanel { Orientation = Orientation.Horizontal };
            var ratio = Theme.Text(st.Mapped + "/" + st.Total);
            ratio.FontWeight = FontWeights.SemiBold;
            ratio.Margin = new Thickness(0, 0, 8, 0);
            colCell.Children.Add(ratio);
            colCell.Children.Add(Kit.LinkButton("편집 ›", () => _host.GoToColumns(m.Id)));
            var badge = Kit.Pill(model.Level == CheckLevels.Error ? "err" : model.Level == CheckLevels.Warn ? "warn" : "ok", model.Level, null);
            var statusCell = new StackPanel();
            statusCell.Children.Add(badge);

            grid.AddRow(new FrameworkElement[]
            {
                use, srcCell, rows, arrow, tgtCombo, modeCombo, keyCombo, colCell, statusCell
            }, m, () =>
            {
                _host.State.Ui.SelMapping = m.Id;
                _host.State.MarkUiChanged();
                SetFooterHint("고른 매핑: " + m.Label);
            }, () => _host.GoToColumns(m.Id));
        }

        private void OnTargetChanged(Mapping m, string v)
        {
            m.Target = v;
            var t = _host.State.TargetTable(v);
            var s = _host.State.SourceOf(m);
            if (t != null && s != null)
            {
                m.Columns = MappingService.AutoMapColumns(s.Columns, t.Columns).Select(x => x.Mapping).ToList();
            }

            m.MergeKey = t != null ? t.Columns.Where(c => c.PrimaryKey).Select(c => c.Name).ToList() : new List<string>();
            _host.State.MarkChanged();
            Kit.Toast(_host.Owner, "대상을 바꿔 컬럼을 다시 자동 매핑했습니다", "ok");
            Refresh();
        }

        private void OnModeChanged(Mapping m, string v, TableMetadata tgt)
        {
            m.Mode = v;
            var mode = WriteModes.Of(v);
            if (mode.NeedsKey && (m.MergeKey == null || m.MergeKey.Count == 0) && tgt != null)
            {
                m.MergeKey = tgt.Columns.Where(c => c.PrimaryKey).Select(c => c.Name).ToList();
            }

            _host.State.MarkChanged();
            Refresh();
        }

        private FrameworkElement BuildKeyCombo(Mapping m, TableMetadata tgt)
        {
            var mode = WriteModes.Of(m.Mode);
            if (!mode.NeedsKey)
            {
                var dash = Theme.Secondary("—");
                dash.VerticalAlignment = VerticalAlignment.Center;
                dash.Margin = new Thickness(6, 0, 0, 0);
                dash.ToolTip = mode.Label + "에는 키가 필요 없음";
                return dash;
            }

            var opts = new List<Kit.ComboOption> { new Kit.ComboOption { Value = "", Label = "—" } };
            if (tgt != null)
            {
                foreach (var c in tgt.Columns)
                {
                    opts.Add(new Kit.ComboOption { Value = c.Name, Label = c.Name + (c.PrimaryKey ? " (PK)" : "") });
                }
            }

            var key = m.MergeKey != null && m.MergeKey.Count > 0 ? m.MergeKey[0] : "";
            return Kit.CellComboBox(opts, key, v =>
            {
                m.MergeKey = string.IsNullOrEmpty(v) ? new List<string>() : new List<string> { v };
                _host.State.MarkChanged();
            });
        }

        private List<Kit.ComboOption> TargetOptions()
        {
            var meta = _host.State.TargetMeta();
            var list = new List<Kit.ComboOption> { new Kit.ComboOption { Value = "", Label = "—" } };
            if (meta != null)
            {
                foreach (var t in meta.Tables.Where(x => string.Equals(x.Kind, "TABLE", StringComparison.Ordinal)))
                {
                    list.Add(new Kit.ComboOption { Value = t.Name, Label = t.Name });
                }
            }

            return list;
        }

        private UIElement BuildBrowseCard()
        {
            var meta = _host.State.SourceMeta();
            var unmapped = TablesLogic.UnmappedSourceTableCount(meta, _host.State.Job.Mappings);
            var sub = unmapped > 0 ? "매핑 안 된 원본 테이블 " + unmapped + "개" : "원본 테이블을 모두 매핑함";
            var targetMeta = _host.State.TargetMeta();
            var panel = new Grid();
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var srcLines = TablesLogic.SourceBrowseLines(meta, _host.State.Job.Mappings, true);
            var tgtLines = TablesLogic.SourceBrowseLines(targetMeta, _host.State.Job.Mappings, false);
            var src = BrowseList("원본 · " + (meta != null ? meta.Schema : "") + " (" + srcLines.Count + ")", srcLines);
            var tgt = BrowseList("대상 · " + (targetMeta != null ? targetMeta.Schema : "") + " (" + tgtLines.Count + ")", tgtLines);
            panel.Children.Add(src);
            var divider = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Center };
            divider.SetResourceReference(Border.BackgroundProperty, Theme.Border);
            Grid.SetColumn(divider, 1);
            panel.Children.Add(divider);
            Grid.SetColumn(tgt, 2);
            panel.Children.Add(tgt);
            return Kit.Card(Icons.List + "  스키마 탐색 · " + sub, null, panel, null);
        }

        private UIElement BrowseList(string title, List<SchemaBrowseLine> lines)
        {
            var scroll = new ScrollViewer { MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var stack = new StackPanel { Margin = new Thickness(0, 0, 4, 0) };
            stack.Children.Add(Kit.SectionLabel(title));
            foreach (var line in lines)
            {
                var row = new Grid { MinHeight = 26, Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var kind = Theme.Secondary(line.Kind);
                kind.FontSize = 10;
                kind.Width = 32;
                kind.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(kind);
                var name = Theme.Text(line.Name);
                name.VerticalAlignment = VerticalAlignment.Center;
                name.ToolTip = line.Name;
                name.FontFamily = Theme.Mono;
                name.FontSize = 12;
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetColumn(name, 1);
                row.Children.Add(name);
                if (!string.IsNullOrEmpty(line.LinkMappingId))
                {
                    var link = Kit.LinkButton(line.LinkText, () =>
                    {
                        _host.State.Ui.SelMapping = line.LinkMappingId;
                        _host.State.MarkUiChanged();
                        Refresh();
                    });
                    link.TextTrimming = TextTrimming.CharacterEllipsis;
                    link.MaxWidth = 170;
                    link.FontSize = 11.5;
                    link.Margin = new Thickness(8, 0, 0, 0);
                    link.VerticalAlignment = VerticalAlignment.Center;
                    link.ToolTip = line.LinkText;
                    Grid.SetColumn(link, 2);
                    row.Children.Add(link);
                }
                else if (line.ShowAddMapping)
                {
                    var add = Kit.GhostButton("매핑", Icons.Add);
                    add.Padding = new Thickness(6, 0, 6, 0);
                    add.MinHeight = 22;
                    add.Height = 22;
                    add.Margin = new Thickness(8, 0, 0, 0);
                    add.VerticalAlignment = VerticalAlignment.Center;
                    add.Click += (s, e) => ShowAddDialog(line.AddSourceTable, false);
                    Grid.SetColumn(add, 2);
                    row.Children.Add(add);
                }

                stack.Children.Add(row);
            }

            scroll.Content = stack;
            return scroll;
        }

        private void ShowAddDialog(string source, bool sql)
        {
            if (!_host.Operations.HasMetadata())
            {
                Kit.Toast(_host.Owner, "메타데이터를 먼저 불러오세요", "warn");
                return;
            }

            var dlg = Modals.AddMappingDialog.Show(_host.Owner, _host.State, source, sql);
            if (!dlg.Ok)
            {
                return;
            }

            Mapping m;
            if (dlg.IsSql)
            {
                m = _host.Operations.AddSqlMapping(dlg.SqlName, dlg.Target, dlg.Mode, dlg.StartTable);
                _host.OpenSqlEditor(m.Id);
            }
            else
            {
                m = _host.Operations.AddTableMapping(dlg.SourceTable, dlg.Target, dlg.Mode);
            }

            _host.State.Ui.SelMapping = m.Id;
            Refresh();
        }

        private void DeleteSelected()
        {
            var id = _host.State.Ui.SelMapping;
            if (string.IsNullOrEmpty(id))
            {
                Kit.Toast(_host.Owner, "삭제할 매핑을 고르세요", "warn");
                return;
            }

            var m = _host.State.Mapping(id);
            if (m == null)
            {
                return;
            }

            if (Dialogs.ConfirmDanger(_host.Owner, "매핑 삭제", "‘" + m.Label + "’ 매핑을 삭제할까요?"))
            {
                _host.Operations.DeleteMapping(id);
                Refresh();
            }
        }
    }
}
