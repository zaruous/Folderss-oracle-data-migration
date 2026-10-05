using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using MigrationStudio.Core.Mapping;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Text;
using MigrationStudio.Services;

namespace MigrationStudio.Ui.Modals
{
    internal static class AddMappingDialog
    {
        public sealed class Result
        {
            public bool Ok { get; set; }
            public bool IsSql { get; set; }
            public string SourceTable { get; set; }
            public string SqlName { get; set; }
            public string StartTable { get; set; }
            public string Target { get; set; }
            public string Mode { get; set; }
        }

        public static Result Show(Window owner, StudioState state, string presetSource, bool presetSql)
        {
            var metaSrc = state.SourceMeta();
            var metaTgt = state.TargetMeta();
            var result = new Result();
            var window = DialogKit.Create(owner, "매핑 추가", 460);
            var body = DialogKit.Body(window);
            var kind = presetSql ? "sql" : "table";

            var tablePanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var sqlPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
            var targetBox = new ComboBox { Margin = new Thickness(0, 4, 0, 0) };
            var modeBox = new ComboBox { Margin = new Thickness(0, 4, 0, 0) };
            var hint = Theme.Secondary("");
            hint.FontSize = 11.5;
            hint.Margin = new Thickness(0, 4, 0, 0);

            var sourceCombo = new ComboBox();
            FillSourceCombo(sourceCombo, metaSrc, state.Job.Mappings);
            if (!string.IsNullOrEmpty(presetSource))
            {
                sourceCombo.SelectedValue = presetSource;
            }
            else
            {
                sourceCombo.SelectedItem = sourceCombo.Items.OfType<Kit.ComboOption>().FirstOrDefault();
            }

            var sqlName = new TextBox { FontFamily = Theme.Mono, Text = NextSqlName(state.Job.Mappings) };
            var startSql = new ComboBox();
            startSql.Items.Add(new Kit.ComboOption { Value = "", Label = "빈 SELECT 문" });
            if (metaSrc != null)
            {
                foreach (var t in metaSrc.Tables.Where(x => string.Equals(x.Kind, "TABLE", StringComparison.Ordinal)))
                {
                    startSql.Items.Add(new Kit.ComboOption { Value = t.Name, Label = t.Name + "의 열로 시작" });
                }
            }

            startSql.DisplayMemberPath = "Label";
            startSql.SelectedValuePath = "Value";
            startSql.SelectedIndex = 0;
            tablePanel.Children.Add(Kit.Field("원본 테이블", sourceCombo, true, null));
            sqlPanel.Children.Add(Kit.Field("SQL 원본 이름", sqlName, true, null));
            sqlPanel.Children.Add(Kit.Field("시작 SQL", startSql, false, null));
            sqlPanel.Children.Add(Kit.Notice("info", new List<Inline> { new Run("추가 뒤 SQL 원본 편집기에서 SELECT 문을 고칩니다.") }));

            FillTargets(targetBox, modeBox, metaTgt, state.Job.Mappings);
            Action suggest = () =>
            {
                if (kind == "sql" || metaTgt == null)
                {
                    hint.Text = "";
                    return;
                }

                var picked = sourceCombo.SelectedItem as Kit.ComboOption;
                if (picked == null || string.IsNullOrEmpty(picked.Value))
                {
                    return;
                }

                var match = MappingService.SuggestTable(picked.Value, metaTgt.Tables, state.Job.Mappings.Where(m => !m.IsSql));
                if (match != null)
                {
                    targetBox.SelectedValue = match.Target;
                    hint.Text = "추천: " + match.Target + " — " + match.Reason;
                }
                else
                {
                    hint.Text = "이름이 맞는 대상이 없습니다. 직접 고르세요.";
                }
            };
            sourceCombo.SelectionChanged += (s, e) => suggest();
            body.Children.Add(Kit.Segmented(new[]
            {
                new Kit.SegmentOption { Value = "table", Label = "테이블" },
                new Kit.SegmentOption { Value = "sql", Label = "SQL (SELECT 문)" }
            }, kind, v =>
            {
                kind = v;
                tablePanel.Visibility = kind == "sql" ? Visibility.Collapsed : Visibility.Visible;
                sqlPanel.Visibility = kind == "sql" ? Visibility.Visible : Visibility.Collapsed;
                suggest();
            }));
            body.Children.Add(tablePanel);
            body.Children.Add(sqlPanel);
            body.Children.Add(Kit.Field("대상 테이블", targetBox, true, null));
            body.Children.Add(hint);
            body.Children.Add(Kit.Field("이관 방식", modeBox, true, "대상에 행이 있으면 INSERT + UPDATE(MERGE)를 권합니다"));

            if (presetSql)
            {
                tablePanel.Visibility = Visibility.Collapsed;
                sqlPanel.Visibility = Visibility.Visible;
            }

            // 필드 사이 간격(Kit.Field는 자체 여백이 없다)
            foreach (UIElement child in body.Children)
            {
                var fe = child as FrameworkElement;
                if (fe != null && fe.Margin.Bottom == 0 && !(child is Border))
                {
                    fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top, fe.Margin.Right, 10);
                }
            }

            suggest();

            var add = DialogKit.PrimaryButton("추가");
            add.Click += (s, e) =>
            {
                var tgtOpt = targetBox.SelectedItem as Kit.ComboOption;
                var tgt = tgtOpt != null ? tgtOpt.Value : targetBox.SelectedValue as string;
                var modeOpt = modeBox.SelectedItem as Kit.ComboOption;
                var mode = modeOpt != null ? modeOpt.Value : WriteModes.Merge;
                if (string.IsNullOrEmpty(tgt))
                {
                    Dialogs.Show(window, "매핑 추가", "대상 테이블을 고르세요.", true);
                    return;
                }

                if (kind == "sql")
                {
                    var name = (sqlName.Text ?? "").Trim().ToUpperInvariant();
                    if (state.Job.Mappings.Any(m => m.IsSql && string.Equals(m.Source, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        Dialogs.Show(window, "매핑 추가", "같은 이름의 SQL 원본이 있습니다.", true);
                        return;
                    }

                    result.IsSql = true;
                    result.SqlName = name;
                    var st = startSql.SelectedItem as Kit.ComboOption;
                    result.StartTable = st != null ? st.Value : "";
                    result.Target = tgt;
                    result.Mode = mode;
                    result.Ok = true;
                }
                else
                {
                    var srcOpt = sourceCombo.SelectedItem as Kit.ComboOption;
                    var src = srcOpt != null ? srcOpt.Value : sourceCombo.SelectedValue as string;
                    if (string.IsNullOrEmpty(src))
                    {
                        Dialogs.Show(window, "매핑 추가", "원본 테이블을 고르세요.", true);
                        return;
                    }

                    if (state.Job.Mappings.Any(m =>
                            !m.IsSql && string.Equals(m.Source, src, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(m.Target, tgt, StringComparison.OrdinalIgnoreCase)))
                    {
                        Dialogs.Show(window, "매핑 추가", "같은 매핑이 이미 있습니다.", true);
                        return;
                    }

                    result.SourceTable = src;
                    result.Target = tgt;
                    result.Mode = mode;
                    result.Ok = true;
                }

                window.DialogResult = true;
            };

            body.Children.Add(DialogKit.Buttons(DialogKit.CancelButton("취소", true), add));
            if (AppServices.DevHostCaptureMode)
            {
                AppServices.DevHostShotWindow = window;
                window.Show();
                return result;
            }

            if (window.ShowDialog() != true)
            {
                result.Ok = false;
            }

            return result;
        }

        private static void FillSourceCombo(ComboBox box, SchemaMetadata meta, IList<Mapping> mappings)
        {
            box.Items.Clear();
            if (meta == null)
            {
                return;
            }

            var mapped = new HashSet<string>(mappings?.Where(m => !m.IsSql).Select(m => m.Source) ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            box.Items.Add(new ComboBoxItem { Content = "— 매핑 안 됨 —", IsEnabled = false });
            foreach (var t in meta.Tables.Where(x => string.Equals(x.Kind, "TABLE", StringComparison.Ordinal) && !mapped.Contains(x.Name)))
            {
                box.Items.Add(new Kit.ComboOption { Value = t.Name, Label = t.Name });
            }

            box.Items.Add(new ComboBoxItem { Content = "— 이미 매핑함 —", IsEnabled = false });
            foreach (var t in meta.Tables.Where(x => string.Equals(x.Kind, "TABLE", StringComparison.Ordinal) && mapped.Contains(x.Name)))
            {
                box.Items.Add(new Kit.ComboOption { Value = t.Name, Label = t.Name + " (다른 대상)" });
            }

            box.DisplayMemberPath = "Label";
            box.SelectedValuePath = "Value";
        }

        private static void FillTargets(ComboBox targetBox, ComboBox modeBox, SchemaMetadata metaTgt, IList<Mapping> existing)
        {
            targetBox.Items.Clear();
            modeBox.Items.Clear();
            foreach (var m in WriteModes.All)
            {
                modeBox.Items.Add(new Kit.ComboOption { Value = m.Value, Label = m.Label });
            }

            modeBox.DisplayMemberPath = "Label";
            modeBox.SelectedValuePath = "Value";
            modeBox.SelectedValue = WriteModes.Merge;
            if (metaTgt == null)
            {
                return;
            }

            foreach (var t in metaTgt.Tables.Where(x => string.Equals(x.Kind, "TABLE", StringComparison.Ordinal)))
            {
                var rows = t.Rows != null ? Format.Number(t.Rows.Value) : "0";
                targetBox.Items.Add(new Kit.ComboOption { Value = t.Name, Label = t.Name + "  (" + rows + "행)" });
            }

            targetBox.DisplayMemberPath = "Label";
            targetBox.SelectedValuePath = "Value";
            targetBox.SelectionChanged += (s, e) =>
            {
                var opt = targetBox.SelectedItem as Kit.ComboOption;
                var tgt = metaTgt.FindTable(opt?.Value);
                if (tgt != null)
                {
                    // 대상에 행이 있으면 다시 실행해도 중복이 생기지 않는 MERGE, 비어 있으면 가장 빠른 INSERT ONLY
                    modeBox.SelectedValue = tgt.Rows != null && tgt.Rows.Value > 0 ? WriteModes.Merge : WriteModes.InsertOnly;
                }
            };
        }

        private static string NextSqlName(IList<Mapping> mappings)
        {
            var n = 1;
            while (mappings != null && mappings.Any(m => m.IsSql && m.Source == "SQLMAP_" + n))
            {
                n++;
            }

            return "SQLMAP_" + n;
        }
    }
}
