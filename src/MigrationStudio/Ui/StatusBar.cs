using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using MigrationStudio.Logic;
using MigrationStudio.Services;

namespace MigrationStudio.Ui
{
    internal sealed class StatusBar : Border
    {
        private readonly StudioState _state;
        private readonly TextBlock _runState = new TextBlock();
        private readonly TextBlock _right = new TextBlock();

        public StatusBar(StudioState state)
        {
            _state = state;
            Height = 24;
            Padding = new Thickness(8, 0, 8, 0);
            BorderThickness = new Thickness(0, 1, 0, 0);
            SetResourceReference(BorderBrushProperty, Theme.Border);
            Theme.Background(this, Theme.SurfaceBackground);

            var row = new DockPanel { LastChildFill = false };
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(Kit.Dot("unknown"));
            left.Children.Add(new TextBlock { Text = " ", Width = 4 });
            left.Children.Add(Kit.DbBadge(null));
            left.Children.Add(new TextBlock { Text = " " + Icons.Arrow + " ", FontFamily = Theme.IconFont, FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
            left.Children.Add(Kit.Dot("unknown"));
            left.Children.Add(new TextBlock { Text = " ", Width = 4 });
            left.Children.Add(Kit.DbBadge(null));
            DockPanel.SetDock(left, Dock.Left);
            row.Children.Add(left);

            _runState.Text = "대기";
            _runState.FontSize = 11.5;
            _runState.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
            _runState.Margin = new Thickness(16, 0, 0, 0);
            _runState.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(_runState);

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            _right.Text = "Oracle 어댑터  |  v" + (version != null ? version.ToString(3) : "0.1.0");
            _right.FontSize = 11.5;
            _right.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
            _right.HorizontalAlignment = HorizontalAlignment.Right;
            _right.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(_right, Dock.Right);
            row.Children.Add(_right);

            Child = row;
        }

        public void Refresh()
        {
            var row = (DockPanel)Child;
            var left = (StackPanel)row.Children[0];
            left.Children.Clear();
            var srcSession = _state.Conn[Roles.Source];
            var tgtSession = _state.Conn[Roles.Target];
            left.Children.Add(Kit.Dot(DotFor(srcSession != null ? srcSession.Status : ConnStatus.Unknown)));
            left.Children.Add(new TextBlock { Text = " ", Width = 4 });
            left.Children.Add(Kit.DbBadge(_state.ProfileForRole(Roles.Source)));
            left.Children.Add(new TextBlock { Text = " " + Icons.Arrow + " ", FontFamily = Theme.IconFont, FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
            left.Children.Add(Kit.Dot(DotFor(tgtSession != null ? tgtSession.Status : ConnStatus.Unknown)));
            left.Children.Add(new TextBlock { Text = " ", Width = 4 });
            left.Children.Add(Kit.DbBadge(_state.ProfileForRole(Roles.Target)));
            var run = _state.Run;
            if (run == null || run.State == RunStates.Idle)
            {
                _runState.Text = "대기";
            }
            else
            {
                var totals = run.Snapshot != null ? run.Snapshot.Totals : null;
                _runState.Text = RunLogic.StatusBarText(run.State, run.Dry, totals != null ? totals.Done : 0, totals != null ? totals.Total : 0, totals != null ? totals.Pct : 0);
            }
        }

        private static string DotFor(string status)
        {
            if (status == ConnStatus.Ok)
            {
                return "ok";
            }

            if (status == ConnStatus.Error)
            {
                return "err";
            }

            if (status == ConnStatus.Testing)
            {
                return "run";
            }

            return "unknown";
        }
    }
}
