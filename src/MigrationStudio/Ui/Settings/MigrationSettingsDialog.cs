using System.Windows;
using System.Windows.Controls;
using MigrationStudio.Core.Model;

namespace MigrationStudio.Ui.Settings
{
    internal static class MigrationSettingsDialog
    {
        public static bool Show(Window owner, MigrationJob job, string initialTab)
        {
            var window = DialogKit.Create(owner, "마이그레이션 설정", 900);
            window.Height = 620;
            window.SizeToContent = SizeToContent.Manual;
            var view = new MigrationSettingsView(job);
            view.LoadFromRepository();
            view.SelectTab(initialTab);
            view.Margin = new Thickness(16, 16, 16, 8);

            var panel = new DockPanel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16) };
            var save = DialogKit.PrimaryButton("저장");
            var cancel = DialogKit.CancelButton("취소", true);
            cancel.Click += (s, e) => window.DialogResult = false;
            save.Click += (s, e) =>
            {
                if (view.Commit())
                {
                    Kit.Toast(window, "마이그레이션 설정을 저장했습니다", "ok");
                    window.DialogResult = true;
                }
            };
            save.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom);
            panel.Children.Add(buttons);
            panel.Children.Add(view);
            window.Content = panel;
            return window.ShowDialog() == true;
        }
    }
}
