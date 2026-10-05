using System;
using System.Windows;
using System.Windows.Controls;
using MigrationStudio.Logic;

namespace MigrationStudio.Ui
{
    internal class PageFrame : Grid
    {
        private readonly StackPanel _headerActions = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly TextBlock _stepTag = new TextBlock();
        private readonly TextBlock _title = new TextBlock();
        private readonly TextBlock _description = new TextBlock();
        private readonly StackPanel _bodyHost = new StackPanel();
        private readonly Grid _fillHost = new Grid();
        private ScrollViewer _scroll;
        private readonly Button _prevBtn;
        private readonly Button _nextBtn;
        private readonly TextBlock _footerHint = new TextBlock();

        public PageFrame()
        {
            // 행: 머리(제목·설명·동작) · 본문(스크롤 또는 창을 채우는 분할 패널) · 바닥(이전/다음)
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SetResourceReference(BackgroundProperty, Theme.WindowBackground);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var inner = new StackPanel { Margin = new Thickness(20, 0, 20, 0), MaxWidth = 1480 };
            var headGrid = new Grid { Margin = new Thickness(20, 16, 20, 14), MaxWidth = 1480 };
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var headLeft = new StackPanel();
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            var stepTagBorder = new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            stepTagBorder.SetResourceReference(Border.BackgroundProperty, Theme.Selection);
            _stepTag.FontSize = 11;
            _stepTag.FontWeight = FontWeights.SemiBold;
            _stepTag.SetResourceReference(TextBlock.ForegroundProperty, Theme.Accent);
            stepTagBorder.Child = _stepTag;
            titleRow.Children.Add(stepTagBorder);
            _title.FontSize = 18;
            _title.FontWeight = FontWeights.SemiBold;
            _title.VerticalAlignment = VerticalAlignment.Center;
            titleRow.Children.Add(_title);
            headLeft.Children.Add(titleRow);
            _description.TextWrapping = TextWrapping.Wrap;
            _description.MaxWidth = 760;
            _description.HorizontalAlignment = HorizontalAlignment.Left;
            _description.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
            headLeft.Children.Add(_description);
            Grid.SetColumn(headLeft, 0);
            headGrid.Children.Add(headLeft);

            _headerActions.HorizontalAlignment = HorizontalAlignment.Right;
            _headerActions.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(_headerActions, 1);
            headGrid.Children.Add(_headerActions);
            Grid.SetRow(headGrid, 0);
            Children.Add(headGrid);

            inner.Children.Add(_bodyHost);
            scroll.Content = inner;
            _scroll = scroll;
            Grid.SetRow(scroll, 1);
            Children.Add(scroll);

            // 창을 채우는 본문(분할 패널) — SetBodyFill을 부르면 스크롤 본문 대신 이것을 쓴다
            _fillHost.Margin = new Thickness(20, 0, 20, 14);
            _fillHost.MaxWidth = 1480;
            _fillHost.Visibility = Visibility.Collapsed;
            Grid.SetRow(_fillHost, 1);
            Children.Add(_fillHost);

            var footer = new Border
            {
                Padding = new Thickness(20, 10, 20, 10),
                BorderThickness = new Thickness(0, 1, 0, 0)
            };
            footer.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            Theme.Background(footer, Theme.WindowBackground);
            var footRow = new DockPanel();
            _prevBtn = Kit.GhostButton("‹ 이전", Icons.Back);
            _prevBtn.Click += (s, e) => { if (PrevClicked != null) PrevClicked(); };
            DockPanel.SetDock(_prevBtn, Dock.Left);
            footRow.Children.Add(_prevBtn);
            _nextBtn = Kit.PrimaryButton("다음", null);
            _nextBtn.Click += (s, e) => { if (NextClicked != null) NextClicked(); };
            DockPanel.SetDock(_nextBtn, Dock.Right);
            footRow.Children.Add(_nextBtn);
            _footerHint.FontSize = 12;
            _footerHint.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
            _footerHint.TextTrimming = TextTrimming.CharacterEllipsis;
            _footerHint.VerticalAlignment = VerticalAlignment.Center;
            _footerHint.Margin = new Thickness(12, 0, 12, 0);
            footRow.Children.Add(_footerHint);
            footer.Child = footRow;
            Grid.SetRow(footer, 2);
            Children.Add(footer);
        }

        public event Action PrevClicked;
        public event Action NextClicked;

        public void SetStep(int stepIndex, string title, string description)
        {
            _title.Text = title ?? "";
            _description.Text = description ?? "";
            _stepTag.Text = "STEP " + (stepIndex + 1);

            _prevBtn.Visibility = stepIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
            _nextBtn.Visibility = stepIndex < Labels.StepTitles.Length - 1 ? Visibility.Visible : Visibility.Collapsed;
            if (stepIndex > 0)
            {
                _prevBtn.Content = Kit.ButtonContentForGhost("‹ " + Labels.StepTitles[stepIndex - 1], Icons.Back);
                Kit.NameForAutomation(_prevBtn, "이전: " + Labels.StepTitles[stepIndex - 1]);
            }

            if (stepIndex < Labels.StepTitles.Length - 1)
            {
                _nextBtn.Content = "다음: " + Labels.StepTitles[stepIndex + 1];
                Kit.NameForAutomation(_nextBtn, "다음: " + Labels.StepTitles[stepIndex + 1]);
            }
        }

        public void SetHeaderActions(params UIElement[] actions)
        {
            _headerActions.Children.Clear();
            UIElement prev = null;
            foreach (var a in actions)
            {
                if (a != null)
                {
                    if (a is FrameworkElement fe)
                    {
                        fe.Margin = new Thickness(prev == null ? 0 : 6, 0, 0, 0);
                        fe.VerticalAlignment = VerticalAlignment.Top;
                    }

                    _headerActions.Children.Add(a);
                    prev = a;
                }
            }
        }

        /// <summary>스크롤되는 본문(기본). 내용이 길면 페이지가 세로로 스크롤한다.</summary>
        public void SetBody(UIElement body)
        {
            _fillHost.Children.Clear();
            _fillHost.Visibility = Visibility.Collapsed;
            _scroll.Visibility = Visibility.Visible;
            _bodyHost.Children.Clear();
            if (body != null)
            {
                if (body is FrameworkElement fe)
                {
                    fe.Margin = new Thickness(0, 0, 0, 14);
                }

                _bodyHost.Children.Add(body);
            }
        }

        /// <summary>창 높이를 채우는 본문 — 분할 패널(<see cref="SplitPane"/>)처럼 위·아래가 남은 높이를 나눠 가져야 할 때.</summary>
        public void SetBodyFill(UIElement body)
        {
            _bodyHost.Children.Clear();
            _scroll.Visibility = Visibility.Collapsed;
            _fillHost.Children.Clear();
            if (body != null)
            {
                _fillHost.Children.Add(body);
            }

            _fillHost.Visibility = Visibility.Visible;
        }

        public void SetFooterHint(string hint)
        {
            _footerHint.Text = hint ?? "";
        }
    }
}
