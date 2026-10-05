using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MigrationStudio.Ui
{
    /// <summary>
    /// 위·아래 두 패널을 가로 분할선으로 나누는 분할 패널. 분할선을 끌면 두 패널이 남은 높이를 나눠 갖는다(별 비율).
    /// 페이지 본문이 창 높이를 채우는 모드(<see cref="PageFrame.SetBodyFill"/>)에서 쓴다 — 스크롤되는 StackPanel 안에서는 별 비율이 의미가 없다.
    /// 끌어서 바뀐 비율은 <c>onRatioChanged</c>로 돌려주어 화면을 다시 그려도 유지한다(StudioUiState).
    /// </summary>
    internal sealed class SplitPane : Grid
    {
        private readonly Grid _topHost = new Grid();
        private readonly Grid _bottomHost = new Grid();
        private readonly RowDefinition _topRow;
        private readonly RowDefinition _splitRow;
        private readonly RowDefinition _bottomRow;
        private readonly GridSplitter _splitter;
        private readonly Action<double> _onRatioChanged;
        private readonly double _minBottom;
        private double _ratio;

        public SplitPane(double topRatio, Action<double> onRatioChanged, double minTop = 140, double minBottom = 120)
        {
            _onRatioChanged = onRatioChanged;
            _minBottom = minBottom;
            _ratio = Clamp(topRatio);
            _topRow = new RowDefinition { Height = new GridLength(_ratio, GridUnitType.Star), MinHeight = minTop };
            _splitRow = new RowDefinition { Height = GridLength.Auto };
            _bottomRow = new RowDefinition { Height = new GridLength(1 - _ratio, GridUnitType.Star), MinHeight = minBottom };
            RowDefinitions.Add(_topRow);
            RowDefinitions.Add(_splitRow);
            RowDefinitions.Add(_bottomRow);

            Children.Add(_topHost);
            _splitter = BuildSplitter();
            SetRow(_splitter, 1);
            Children.Add(_splitter);
            SetRow(_bottomHost, 2);
            Children.Add(_bottomHost);
        }

        /// <summary>위 패널 내용. 바꾸면 전 내용은 떼어 낸다(한 요소를 두 곳에 붙이면 WPF가 던진다).</summary>
        public UIElement Top
        {
            set
            {
                _topHost.Children.Clear();
                if (value != null)
                {
                    _topHost.Children.Add(value);
                }
            }
        }

        public UIElement Bottom
        {
            set
            {
                _bottomHost.Children.Clear();
                if (value != null)
                {
                    _bottomHost.Children.Add(value);
                }
            }
        }

        /// <summary>아래 패널이 없을 때(빈 상태 등) 분할선과 함께 숨긴다 — 위 패널이 전체를 쓴다.</summary>
        public bool BottomVisible
        {
            set
            {
                _splitter.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                _bottomHost.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                if (value)
                {
                    _topRow.Height = new GridLength(_ratio, GridUnitType.Star);
                    _bottomRow.Height = new GridLength(1 - _ratio, GridUnitType.Star);
                    _bottomRow.MinHeight = _minBottom;
                }
                else
                {
                    _topRow.Height = new GridLength(1, GridUnitType.Star);
                    _bottomRow.MinHeight = 0;
                    _bottomRow.Height = new GridLength(0);
                }
            }
        }

        private GridSplitter BuildSplitter()
        {
            // 분할선: 투명한 9px 띠 + 가운데 짧은 손잡이 줄. 끌면 위·아래 행(별 비율)이 같이 바뀐다.
            var grip = new FrameworkElementFactory(typeof(Border));
            grip.SetValue(Border.WidthProperty, 36.0);
            grip.SetValue(Border.HeightProperty, 3.0);
            grip.SetValue(Border.CornerRadiusProperty, new CornerRadius(1.5));
            grip.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            grip.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            grip.SetResourceReference(Border.BackgroundProperty, Theme.Border);
            var root = new FrameworkElementFactory(typeof(Border));
            root.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            root.AppendChild(grip);
            var template = new ControlTemplate(typeof(GridSplitter)) { VisualTree = root };

            var splitter = new GridSplitter
            {
                Height = 9,
                Margin = new Thickness(0, 2, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                ResizeDirection = GridResizeDirection.Rows,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeNS,
                Template = template,
                ToolTip = "끌어서 위·아래 높이 조절",
                Tag = "overlay"
            };
            Kit.NameForAutomation(splitter, "분할선");
            splitter.DragCompleted += OnDragCompleted;
            return splitter;
        }

        private void OnDragCompleted(object sender, DragCompletedEventArgs e)
        {
            var total = _topRow.ActualHeight + _bottomRow.ActualHeight;
            if (total <= 0)
            {
                return;
            }

            _ratio = Clamp(_topRow.ActualHeight / total);
            if (_onRatioChanged != null)
            {
                _onRatioChanged(_ratio);
            }
        }

        private static double Clamp(double ratio)
        {
            if (double.IsNaN(ratio) || ratio <= 0)
            {
                return 0.5;
            }

            return Math.Max(0.1, Math.Min(0.9, ratio));
        }
    }
}
