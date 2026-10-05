using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MigrationStudio.Core.Mapping;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Services;

namespace MigrationStudio.Ui.Modals
{
    internal static class AutoMatchDialog
    {
        public static List<TableMatch> Show(Window owner, SchemaMetadata source, SchemaMetadata target, IList<Mapping> existing)
        {
            var matches = MappingService.AutoMatchTables(
                source?.Tables,
                target?.Tables,
                existing);
            if (matches.Count == 0)
            {
                return matches;
            }

            var window = DialogKit.Create(owner, "이름으로 자동 매칭", 620);
            var body = DialogKit.Body(window);
            body.Children.Add(Theme.Secondary("이름·용어 사전·접두어 규칙으로 맞출 후보입니다. 추가할 항목을 고르세요."));
            var grid = new RowGrid(
                new RowGridColumn { Header = "", Width = new GridLength(32) },
                new RowGridColumn { Header = "원본 → 대상", Width = new GridLength(1.1, GridUnitType.Star) },
                new RowGridColumn { Header = "근거", Width = new GridLength(1, GridUnitType.Star) });
            var selected = new Dictionary<TableMatch, CheckBox>();
            foreach (var m in matches)
            {
                var cb = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
                selected[m] = cb;
                var label = Theme.Text(m.Source + " → " + m.Target);
                label.FontFamily = Theme.Mono;
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                label.ToolTip = m.Source + " → " + m.Target;
                var reason = Theme.Secondary(m.Reason);
                reason.TextTrimming = TextTrimming.CharacterEllipsis;
                reason.ToolTip = m.Reason;
                grid.AddRow(new FrameworkElement[] { cb, label, reason }, m, null, null);
            }

            body.Children.Add(grid.Root);
            List<TableMatch> picked = null;
            var add = DialogKit.PrimaryButton("추가");
            add.Click += (s, e) =>
            {
                picked = selected.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToList();
                window.DialogResult = true;
            };
            body.Children.Add(DialogKit.Buttons(DialogKit.CancelButton("취소", true), add));
            if (AppServices.DevHostCaptureMode)
            {
                AppServices.DevHostShotWindow = window;
                window.Show();
                return new List<TableMatch>();
            }

            return window.ShowDialog() == true ? picked ?? new List<TableMatch>() : new List<TableMatch>();
        }
    }
}
