using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MigrationStudio.Ui
{
    /// <summary>얇은 진행 막대. Set(퍼센트, 종류): running(강조) · paused(경고) · done(성공) · stopped(위험) · ""(강조).</summary>
    internal sealed class ProgressBarView
    {
        private readonly Grid _grid;
        private readonly Border _fill;
        private readonly ColumnDefinition _left;
        private readonly ColumnDefinition _right;

        public ProgressBarView(double height)
        {
            _grid = new Grid { Height = height };
            _left = new ColumnDefinition { Width = new GridLength(0.0001, GridUnitType.Star) };
            _right = new ColumnDefinition { Width = new GridLength(100, GridUnitType.Star) };
            _grid.ColumnDefinitions.Add(_left);
            _grid.ColumnDefinitions.Add(_right);
            var track = new Border { CornerRadius = new CornerRadius(height / 2), Tag = "overlay" };
            track.SetResourceReference(Border.BackgroundProperty, Theme.Border);
            Grid.SetColumnSpan(track, 2);
            _grid.Children.Add(track);
            _fill = new Border { CornerRadius = new CornerRadius(height / 2), Tag = "overlay" };
            _grid.Children.Add(_fill);
            Set(0, "");
        }

        public FrameworkElement Root
        {
            get { return _grid; }
        }

        public void Set(double percent, string kind)
        {
            var p = Math.Max(0, Math.Min(100, percent));
            _left.Width = new GridLength(Math.Max(0.0001, p), GridUnitType.Star);
            _right.Width = new GridLength(Math.Max(0.0001, 100 - p), GridUnitType.Star);
            switch (kind)
            {
                case "done":
                    _fill.Background = Theme.Success;
                    break;
                case "paused":
                    _fill.Background = Theme.Warning;
                    break;
                case "stopped":
                case "failed":
                    _fill.Background = Theme.Danger;
                    break;
                default:
                    _fill.SetResourceReference(Border.BackgroundProperty, Theme.Accent);
                    break;
            }
        }
    }
}
