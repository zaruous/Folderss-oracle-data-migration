using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Markup;
using System.Windows.Threading;
using MigrationStudio.Core.Settings;

namespace MigrationStudio.Ui
{
    internal static class Kit
    {
        private static readonly List<Border> ToastStack = new List<Border>();
        private static ControlTemplate _primaryTemplate;
        private static Style _shellIconStyle;

        public static Border AccentTint(FrameworkElement child)
        {
            var grid = new Grid();
            var tint = new Border { CornerRadius = new CornerRadius(15), Tag = "overlay" };
            tint.SetResourceReference(Border.BackgroundProperty, Theme.Accent);
            tint.Opacity = 0.15;
            grid.Children.Add(tint);
            grid.Children.Add(child);
            return new Border { Child = grid, Background = Brushes.Transparent };
        }

        public static TextBlock Icon(string glyph)
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = Theme.IconFont,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        public static Brush AccentTintBrush()
        {
            var source = Application.Current != null ? Application.Current.TryFindResource(Theme.Accent) as Brush : null;
            var brush = source != null ? source.CloneCurrentValue() : Brushes.DodgerBlue.CloneCurrentValue();
            brush.Opacity = 0.15;
            return brush;
        }

        public static Border Card(string title, UIElement tools, UIElement body, UIElement foot)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                Tag = "Card"
            };
            card.SetResourceReference(Border.BackgroundProperty, Theme.PanelBackground);
            card.SetResourceReference(Border.BorderBrushProperty, Theme.Border);

            var root = new DockPanel();
            if (!string.IsNullOrEmpty(title) || tools != null)
            {
                var head = new Border { Padding = new Thickness(12, 6, 12, 6), MinHeight = 40, BorderThickness = new Thickness(0, 0, 0, 1) };
                head.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
                var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
                var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 2) };
                var titleText = title;
                if (!string.IsNullOrEmpty(titleText) && titleText[0] >= '\uE000' && titleText[0] <= '\uF8FF')
                {
                    var icon = Icon(titleText.Substring(0, 1));
                    icon.Margin = new Thickness(0, 0, 6, 0);
                    titleRow.Children.Add(icon);
                    titleText = titleText.Substring(1).TrimStart();
                }

                var titleBlock = Theme.Text(titleText);
                titleBlock.FontWeight = FontWeights.SemiBold;
                titleRow.Children.Add(titleBlock);
                row.Children.Add(titleRow);
                if (tools != null)
                {
                    row.Children.Add(tools);
                }

                head.Child = row;
                DockPanel.SetDock(head, Dock.Top);
                root.Children.Add(head);
            }

            if (foot != null)
            {
                var footHost = new Border { Padding = new Thickness(12, 9, 12, 9), BorderThickness = new Thickness(0, 1, 0, 0) };
                footHost.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
                footHost.Child = foot;
                DockPanel.SetDock(footHost, Dock.Bottom);
                root.Children.Add(footHost);
            }

            if (body != null)
            {
                var bodyHost = new Border { Padding = new Thickness(12) };
                bodyHost.Child = body;
                root.Children.Add(bodyHost);
            }

            card.Child = root;
            return card;
        }

        public static StackPanel Field(string label, UIElement control, bool required, string hint)
        {
            var panel = new StackPanel();
            if (!string.IsNullOrEmpty(label))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
                var lb = Theme.Secondary(label);
                lb.FontSize = 11.5;
                row.Children.Add(lb);
                if (required)
                {
                    row.Children.Add(new TextBlock { Text = " *", Foreground = Theme.Danger, FontSize = 11.5 });
                }

                panel.Children.Add(row);
            }

            if (control != null)
            {
                panel.Children.Add(control);
            }

            if (!string.IsNullOrEmpty(hint))
            {
                var h = Theme.Secondary(hint);
                h.FontSize = 11.5;
                h.Margin = new Thickness(0, 3, 0, 0);
                h.TextWrapping = TextWrapping.Wrap;
                panel.Children.Add(h);
            }

            return panel;
        }

        public static Grid FormGrid(int columns, params UIElement[] fields)
        {
            var grid = new Grid { Margin = new Thickness(0) };
            for (var c = 0; c < columns; c++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            for (var i = 0; i < fields.Length; i++)
            {
                var col = i % columns;
                var row = i / columns;
                while (grid.RowDefinitions.Count <= row)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                }

                Grid.SetColumn(fields[i], col);
                Grid.SetRow(fields[i], row);
                if (fields[i] is FrameworkElement fe)
                {
                    fe.Margin = new Thickness(col > 0 ? 12 : 0, row > 0 ? 10 : 0, 0, 0);
                }
                grid.Children.Add(fields[i]);
            }

            return grid;
        }

        public static Border Notice(string kind, IList<Inline> inlines)
        {
            Brush bg;
            Brush border;
            string glyph;
            switch (kind)
            {
                case "warn":
                    bg = Theme.WarningTint;
                    border = Theme.Warning;
                    glyph = Icons.Warn;
                    break;
                case "err":
                    bg = Theme.DangerTint;
                    border = Theme.Danger;
                    glyph = Icons.Error;
                    break;
                case "ok":
                    bg = Theme.SuccessTint;
                    border = Theme.Success;
                    glyph = Icons.Check;
                    break;
                default:
                    bg = Theme.SuccessTint;
                    border = Theme.Success;
                    glyph = Icons.Info;
                    kind = "info";
                    break;
            }

            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = Theme.IconFont,
                FontSize = 14,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = border
            };
            var text = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Theme.Foreground(text);
            if (inlines != null)
            {
                foreach (var inline in inlines)
                {
                    text.Inlines.Add(inline);
                }
            }

            var row = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(text);

            var box = new Border
            {
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(4),
                Background = bg,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                Child = row
            };
            box.Opacity = 1;
            var borderBrush = border as SolidColorBrush;
            if (borderBrush != null)
            {
                box.BorderBrush = new SolidColorBrush(Color.FromArgb(0x73, borderBrush.Color.R, borderBrush.Color.G, borderBrush.Color.B));
            }

            return box;
        }

        public static Border Pill(string kind, string text, string iconGlyph)
        {
            Brush bg;
            Brush fg;
            switch (kind)
            {
                case "ok":
                    bg = Theme.SuccessTint;
                    fg = Theme.Success;
                    break;
                case "warn":
                    bg = Theme.WarningTint;
                    fg = Theme.Warning;
                    break;
                case "err":
                    bg = Theme.DangerTint;
                    fg = Theme.Danger;
                    break;
                default:
                    bg = Theme.SuccessTint;
                    fg = Theme.Success;
                    kind = "run";
                    break;
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (!string.IsNullOrEmpty(iconGlyph))
            {
                var ig = new TextBlock { Text = iconGlyph, FontFamily = Theme.IconFont, FontSize = 10, Foreground = fg, Margin = new Thickness(0, 0, 4, 0) };
                row.Children.Add(ig);
            }

            var tb = new TextBlock { Text = text, FontSize = 11.5, Foreground = fg, VerticalAlignment = VerticalAlignment.Center };
            if (kind == "run")
            {
                tb.ClearValue(TextBlock.ForegroundProperty);
                tb.SetResourceReference(TextBlock.ForegroundProperty, Theme.Accent);
            }

            row.Children.Add(tb);
            return new Border
            {
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 0, 9, 0),
                Background = bg,
                Child = row,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        /// <summary>검증 수준 배지: PASS · WARN · ERROR · INFO · SKIP. 높이 18, 모서리 3, 굵은 10.5px.</summary>
        public static Border LevelBadge(string level, string textOverride)
        {
            var text = string.IsNullOrEmpty(textOverride) ? level : textOverride;
            var block = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
            var border = new Border
            {
                Height = 18,
                MinWidth = 44,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 0, 6, 0),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = block
            };
            switch (level)
            {
                case "PASS":
                    block.Foreground = Theme.Success;
                    border.Background = Theme.SuccessTint;
                    border.BorderBrush = Brushes.Transparent;
                    break;
                case "WARN":
                    block.Foreground = Theme.Warning;
                    border.Background = Theme.WarningTint;
                    border.BorderBrush = Brushes.Transparent;
                    break;
                case "ERROR":
                    block.Foreground = Theme.Danger;
                    border.Background = Theme.DangerTint;
                    border.BorderBrush = Brushes.Transparent;
                    break;
                case "INFO":
                    block.SetResourceReference(TextBlock.ForegroundProperty, Theme.Accent);
                    border.Background = AccentTintBrush();
                    border.BorderBrush = Brushes.Transparent;
                    break;
                default:
                    block.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
                    border.Background = Brushes.Transparent;
                    border.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
                    break;
            }

            return border;
        }

        public static Border Tag(string text, string kind)
        {
            Brush fg;
            Brush border;
            if (kind == "pk")
            {
                fg = Theme.Warning;
                border = Theme.Warning;
            }
            else
            {
                fg = Theme.Success;
                border = Theme.Success;
            }

            return new Border
            {
                Height = 16,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 0, 5, 0),
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = fg }
            };
        }

        public static System.Windows.Controls.Border RoleTag(bool isSource)
        {
            var label = isSource ? "SOURCE" : "TARGET";
            var text = new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeights.Bold };
            if (isSource)
            {
                text.SetResourceReference(TextBlock.ForegroundProperty, Theme.Accent);
            }
            else
            {
                text.Foreground = Theme.Success;
            }

            var border = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1, 6, 1),
                Child = text
            };
            if (isSource)
            {
                border.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, Theme.Selection);
            }
            else
            {
                border.Background = Theme.SuccessTint;
            }

            return border;
        }

        public static FrameworkElement Dot(string state)
        {
            var dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
            switch (state)
            {
                case "ok":
                    dot.Fill = Theme.Success;
                    break;
                case "warn":
                    dot.Fill = Theme.Warning;
                    break;
                case "err":
                    dot.Fill = Theme.Danger;
                    break;
                case "run":
                    dot.SetResourceReference(Shape.FillProperty, Theme.Accent);
                    var anim = new DoubleAnimation(0.35, 1, TimeSpan.FromSeconds(0.5)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                    dot.BeginAnimation(UIElement.OpacityProperty, anim);
                    break;
                default:
                    dot.SetResourceReference(Shape.FillProperty, Theme.DisabledText);
                    break;
            }

            return dot;
        }

        public static FrameworkElement Spinner()
        {
            var arc = new Ellipse
            {
                Width = 12,
                Height = 12,
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 2, 2 }
            };
            arc.SetResourceReference(Shape.StrokeProperty, Theme.Accent);
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.7)) { RepeatBehavior = RepeatBehavior.Forever };
            var rt = new RotateTransform();
            arc.RenderTransform = rt;
            arc.RenderTransformOrigin = new Point(0.5, 0.5);
            rt.BeginAnimation(RotateTransform.AngleProperty, spin);
            return arc;
        }

        public static Border Segmented(IList<SegmentOption> options, string value, Action<string> onChange)
        {
            var host = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Padding = new Thickness(2) };
            host.SetResourceReference(Border.BackgroundProperty, Theme.ControlBackground);
            host.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var opt in options)
            {
                var btn = new ToggleButton
                {
                    Content = opt.Label,
                    Padding = new Thickness(10, 0, 10, 0),
                    Height = 22,
                    Margin = new Thickness(1, 0, 1, 0),
                    BorderThickness = new Thickness(1),
                    Background = Brushes.Transparent,
                    Template = SegmentTemplate(),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Focusable = false,
                    IsChecked = string.Equals(opt.Value, value, StringComparison.Ordinal),
                    Tag = opt.Value
                };
                btn.SetResourceReference(Control.BorderBrushProperty, Theme.Border);
                btn.Checked += (s, e) =>
                {
                    foreach (ToggleButton b in row.Children)
                    {
                        if (!ReferenceEquals(b, btn))
                        {
                            b.IsChecked = false;
                        }
                    }

                    onChange(opt.Value);
                    StyleSegment(row, opt.Value);
                };
                btn.Unchecked += (s, e) => StyleSegment(row, (string)((ToggleButton)s).Tag);
                row.Children.Add(btn);
            }

            StyleSegment(row, value);
            host.Child = row;
            return host;
        }

        private static ControlTemplate _segmentTemplate;

        /// <summary>
        /// Segmented 버튼 템플릿. WPF 기본 ToggleButton 템플릿은 체크 상태에서 시스템 색(하늘색)을 덧칠해
        /// 지정한 Background·Foreground가 무시되므로, 지정한 색을 그대로 그리는 단순한 템플릿을 쓴다.
        /// </summary>
        private static ControlTemplate SegmentTemplate()
        {
            if (_segmentTemplate != null)
            {
                return _segmentTemplate;
            }

            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            var template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = border };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85));
            template.Triggers.Add(hover);
            template.Seal();
            _segmentTemplate = template;
            return template;
        }

        private static void StyleSegment(StackPanel row, string selectedValue)
        {
            foreach (ToggleButton b in row.Children)
            {
                var sel = b.IsChecked == true;
                if (sel)
                {
                    b.FontWeight = FontWeights.SemiBold;
                    b.SetResourceReference(Control.ForegroundProperty, Theme.Accent);
                    b.Background = AccentTintBrush();
                    b.SetResourceReference(Control.BorderBrushProperty, Theme.Accent);
                }
                else
                {
                    b.FontWeight = FontWeights.Normal;
                    b.SetResourceReference(Control.ForegroundProperty, Theme.SecondaryText);
                    b.Background = Brushes.Transparent;
                    b.SetResourceReference(Control.BorderBrushProperty, Theme.Border);
                }
            }
        }

        public sealed class SegmentOption
        {
            public string Value { get; set; }
            public string Label { get; set; }
        }

        public static StackPanel RadioCards(string group, IList<RadioCardItem> items, string value, Action<string> onChange)
        {
            var panel = new StackPanel();
            foreach (var item in items)
            {
                var card = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0, 0, 0, 6),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                card.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
                var selected = string.Equals(item.Value, value, StringComparison.Ordinal);
                if (selected)
                {
                    card.SetResourceReference(Border.BorderBrushProperty, Theme.Accent);
                    card.SetResourceReference(Border.BackgroundProperty, Theme.Selection);
                }
                else
                {
                    card.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
                    card.ClearValue(Border.BackgroundProperty);
                }

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var texts = new StackPanel();
                texts.Children.Add(Theme.Text(item.Title));
                if (!string.IsNullOrEmpty(item.Description))
                {
                    var d = Theme.Secondary(item.Description);
                    d.FontSize = 11.5;
                    d.TextWrapping = TextWrapping.Wrap;
                    texts.Children.Add(d);
                }

                Grid.SetColumn(texts, 0);
                grid.Children.Add(texts);
                if (item.Extra != null)
                {
                    Grid.SetColumn(item.Extra, 1);
                    grid.Children.Add(item.Extra);
                }

                card.Child = grid;
                var radio = new RadioButton { GroupName = group, IsChecked = selected, Visibility = Visibility.Collapsed };
                card.MouseLeftButtonUp += (s, e) =>
                {
                    onChange(item.Value);
                };
                var wrap = new Grid();
                wrap.Children.Add(card);
                wrap.Children.Add(radio);
                panel.Children.Add(wrap);
            }

            return panel;
        }

        public sealed class RadioCardItem
        {
            public string Value { get; set; }
            public string Title { get; set; }
            public string Description { get; set; }
            public UIElement Extra { get; set; }
        }

        public static Button PrimaryButton(string text, string icon)
        {
            var content = ButtonContent(text, icon, true);
            var button = new Button
            {
                Content = content,
                Template = PrimaryTemplate(),
                Padding = new Thickness(10, 5, 10, 5),
                MinHeight = 28,
                FontSize = 13
            };
            NameForAutomation(button, text);
            return button;
        }

        public static Button Button(string text, string icon)
        {
            var btn = new Button
            {
                Content = ButtonContent(text, icon, false),
                Padding = new Thickness(10, 5, 10, 5),
                MinHeight = 28,
                FontSize = 13
            };
            btn.HorizontalAlignment = HorizontalAlignment.Left;
            NameForAutomation(btn, text);
            return btn;
        }

        public static Button GhostButton(string text, string icon)
        {
            var btn = Button(text, icon);
            btn.SetResourceReference(Control.BackgroundProperty, Theme.WindowBackground);
            btn.MinHeight = 24;
            btn.Padding = new Thickness(8, 3, 8, 3);
            btn.FontSize = 12;
            btn.HorizontalAlignment = HorizontalAlignment.Left;
            return btn;
        }

        public static Button IconButton(string glyph, string tooltip)
        {
            EnsureShellIconStyleInApp();
            var btn = new Button
            {
                Content = glyph,
                ToolTip = tooltip,
                Padding = new Thickness(0)
            };
            btn.Style = ShellIconStyle();
            NameForAutomation(btn, tooltip);
            return btn;
        }

        /// <summary>
        /// 아이콘+글자 패널이나 글리프 하나가 내용인 버튼은 UI 자동화 이름이 비어 화면 낭독기·자동 검사가 못 찾는다 — 글자를 이름으로 준다.
        /// </summary>
        public static void NameForAutomation(UIElement element, string name)
        {
            if (element != null && !string.IsNullOrEmpty(name))
            {
                System.Windows.Automation.AutomationProperties.SetName(element, name);
            }
        }

        private static void EnsureShellIconStyleInApp()
        {
            if (Application.Current == null)
            {
                return;
            }

            if (!Application.Current.Resources.Contains("ShellIconButton"))
            {
                var dict = (ResourceDictionary)XamlReader.Parse(ShellIconButtonXaml);
                Application.Current.Resources.MergedDictionaries.Add(dict);
            }
        }

        public static TextBlock LinkButton(string text, Action onClick)
        {
            var link = new TextBlock
            {
                Text = text,
                Cursor = System.Windows.Input.Cursors.Hand,
                TextDecorations = null
            };
            link.SetResourceReference(TextBlock.ForegroundProperty, Theme.Accent);
            link.MouseLeftButtonUp += (s, e) =>
            {
                if (onClick != null)
                {
                    onClick();
                }
            };
            link.MouseEnter += (s, e) => link.TextDecorations = TextDecorations.Underline;
            link.MouseLeave += (s, e) => link.TextDecorations = null;
            return link;
        }

        public static Grid KeyValue(IList<KeyValuePair<string, string>> rows)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < rows.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var key = Theme.Secondary(rows[i].Key);
                key.FontSize = 12;
                key.Margin = new Thickness(0, i > 0 ? 4 : 0, 12, 0);
                var val = Theme.Text(rows[i].Value ?? "");
                val.FontSize = 12;
                val.TextWrapping = TextWrapping.Wrap;
                val.Margin = new Thickness(0, i > 0 ? 4 : 0, 0, 0);
                Grid.SetRow(key, i);
                Grid.SetRow(val, i);
                Grid.SetColumn(val, 1);
                grid.Children.Add(key);
                grid.Children.Add(val);
            }

            return grid;
        }

        public static TextBlock SectionLabel(string text)
        {
            var block = Theme.Secondary(text);
            block.FontSize = 11;
            block.FontWeight = FontWeights.SemiBold;
            block.SetResourceReference(TextBlock.ForegroundProperty, Theme.DisabledText);
            block.Margin = new Thickness(0, 0, 0, 6);
            return block;
        }

        public static StackPanel EmptyState(string glyph, string title, string text)
        {
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = Theme.IconFont,
                FontSize = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            };
            icon.SetResourceReference(TextBlock.ForegroundProperty, Theme.DisabledText);
            panel.Children.Add(icon);
            var t = Theme.Text(title);
            t.FontWeight = FontWeights.SemiBold;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(t);
            var d = Theme.Secondary(text);
            d.TextWrapping = TextWrapping.Wrap;
            d.HorizontalAlignment = HorizontalAlignment.Center;
            d.TextAlignment = TextAlignment.Center;
            d.MaxWidth = 420;
            panel.Children.Add(d);
            return panel;
        }

        public static Border DbBadge(ConnectionProfile profile)
        {
            return Theme.DbBadge(profile);
        }

        public static void Toast(Window owner, string text, string kind)
        {
            if (owner == null)
            {
                return;
            }

            Brush bg;
            switch (kind)
            {
                case "warn": bg = Theme.WarningTint; break;
                case "err": bg = Theme.DangerTint; break;
                case "ok": bg = Theme.SuccessTint; break;
                default: bg = Theme.SuccessTint; break;
            }

            var toast = new Border
            {
                Tag = "overlay",
                Padding = new Thickness(12, 8, 12, 8),
                CornerRadius = new CornerRadius(4),
                Background = bg,
                BorderThickness = new Thickness(1),
                Child = Theme.Text(text ?? "")
            };
            toast.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, Theme.Border);

            var layer = owner.Content as Panel;
            if (layer == null)
            {
                var grid = new Grid();
                var old = owner.Content as UIElement;
                owner.Content = grid;
                if (old != null)
                {
                    grid.Children.Add(old);
                }

                layer = grid;
            }

            toast.HorizontalAlignment = HorizontalAlignment.Right;
            toast.VerticalAlignment = VerticalAlignment.Bottom;
            // 페이지 바닥 줄(이전/다음 버튼, 약 56px) 위에 뜨게 — 바닥에 붙이면 "다음" 버튼을 가린다
            toast.Margin = new Thickness(0, 0, 16, 64 + ToastStack.Count * 36);
            Panel.SetZIndex(toast, 1000);
            var hostGrid = layer as Grid;
            if (hostGrid != null)
            {
                // Grid의 첫 행(메뉴 막대)에 붙지 않고 창 전체에 걸쳐 오른쪽 아래에 뜨게
                Grid.SetRowSpan(toast, Math.Max(1, hostGrid.RowDefinitions.Count));
                Grid.SetColumnSpan(toast, Math.Max(1, hostGrid.ColumnDefinitions.Count));
            }

            layer.Children.Add(toast);
            ToastStack.Add(toast);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                layer.Children.Remove(toast);
                ToastStack.Remove(toast);
            };
            timer.Start();
        }

        public static UIElement ButtonContentForGhost(string text, string icon)
        {
            return ButtonContent(text, icon, false);
        }

        private static UIElement ButtonContent(string text, string icon, bool onPrimary)
        {
            if (string.IsNullOrEmpty(icon))
            {
                var solo = new TextBlock { Text = text };
                if (onPrimary)
                {
                    solo.SetResourceReference(TextBlock.ForegroundProperty, Theme.PanelBackground);
                }

                return solo;
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var ig = new TextBlock { Text = icon, FontFamily = Theme.IconFont, Margin = new Thickness(0, 0, 6, 0) };
            var tb = new TextBlock { Text = text };
            if (onPrimary)
            {
                ig.SetResourceReference(TextBlock.ForegroundProperty, Theme.PanelBackground);
                tb.SetResourceReference(TextBlock.ForegroundProperty, Theme.PanelBackground);
            }

            row.Children.Add(ig);
            row.Children.Add(tb);
            return row;
        }

        private static Style ShellIconStyle()
        {
            if (_shellIconStyle != null)
            {
                return _shellIconStyle;
            }

            var dict = (ResourceDictionary)XamlReader.Parse(ShellIconButtonXaml);
            _shellIconStyle = (Style)dict["ShellIconButton"];
            return _shellIconStyle;
        }

        private const string ShellIconButtonXaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='ShellIconButton' TargetType='Button'>
    <Setter Property='Foreground' Value='{DynamicResource PrimaryText}'/>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='Width' Value='28'/>
    <Setter Property='Height' Value='26'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='Bd' Background='Transparent' CornerRadius='3' SnapsToDevicePixels='True'>
            <TextBlock x:Name='Glyph' Text='{Binding Content, RelativeSource={RelativeSource TemplatedParent}}'
                       FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='14'
                       HorizontalAlignment='Center' VerticalAlignment='Center' Foreground='{TemplateBinding Foreground}'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{DynamicResource ControlHoverBrush}'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter TargetName='Glyph' Property='Opacity' Value='0.3'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";

        public static ComboBox CellComboBox(IList<ComboOption> options, string value, Action<string> onChange)
        {
            var box = new ComboBox
            {
                Height = 24,
                Padding = new Thickness(4, 2, 4, 2),
                BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 11.5
            };
            box.SetResourceReference(Control.BorderBrushProperty, Theme.Border);
            box.SetResourceReference(Control.BackgroundProperty, Theme.ControlBackground);
            foreach (var o in options ?? Array.Empty<ComboOption>())
            {
                box.Items.Add(o);
            }

            box.DisplayMemberPath = "Label";
            box.SelectedValuePath = "Value";
            box.SelectedValue = value ?? "";
            box.SelectionChanged += (s, e) =>
            {
                if (box.IsDropDownOpen || box.SelectedValue != null)
                {
                    onChange?.Invoke(box.SelectedValue as string ?? "");
                }
            };
            box.GotFocus += (s, e) => box.SetResourceReference(Control.BorderBrushProperty, Theme.Accent);
            box.LostFocus += (s, e) => box.SetResourceReference(Control.BorderBrushProperty, Theme.Border);
            return box;
        }

        public sealed class ComboOption
        {
            public string Value { get; set; }
            public string Label { get; set; }
        }

        /// <summary>자리 글자(힌트)가 있는 검색 입력 칸. 비어 있을 때만 힌트가 보이고, 글을 쓰면 onChange로 알린다.</summary>
        public static FrameworkElement SearchBox(string placeholder, string value, Action<string> onChange, double width)
        {
            var box = new TextBox
            {
                Text = value ?? "",
                Height = 26,
                Padding = new Thickness(26, 3, 6, 3),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 12
            };
            var glyph = Icon(Icons.Search);
            glyph.FontSize = 12;
            glyph.Margin = new Thickness(8, 0, 0, 0);
            glyph.HorizontalAlignment = HorizontalAlignment.Left;
            glyph.IsHitTestVisible = false;
            glyph.SetResourceReference(TextBlock.ForegroundProperty, Theme.DisabledText);
            glyph.Tag = "overlay";
            var hint = new TextBlock { Text = placeholder, FontSize = 12, Margin = new Thickness(26, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Tag = "overlay" };
            hint.SetResourceReference(TextBlock.ForegroundProperty, Theme.DisabledText);
            hint.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;
            box.TextChanged += (s, e) =>
            {
                hint.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;
                if (onChange != null)
                {
                    onChange(box.Text);
                }
            };
            var host = new Grid { Width = width, Margin = new Thickness(8, 0, 0, 0) };
            host.Children.Add(box);
            host.Children.Add(hint);
            host.Children.Add(glyph);
            return host;
        }

        public static TextBox CellTextBox(string value, Action<string> onChange, bool mono)
        {
            var box = new TextBox
            {
                Height = 24,
                Padding = new Thickness(4, 2, 4, 2),
                BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Text = value ?? ""
            };
            if (mono)
            {
                box.FontFamily = Theme.Mono;
            }

            box.SetResourceReference(Control.BorderBrushProperty, Theme.Border);
            box.SetResourceReference(Control.BackgroundProperty, Theme.ControlBackground);
            box.TextChanged += (s, e) => onChange?.Invoke(box.Text);
            box.GotFocus += (s, e) => box.SetResourceReference(Control.BorderBrushProperty, Theme.Accent);
            box.LostFocus += (s, e) => box.SetResourceReference(Control.BorderBrushProperty, Theme.Border);
            return box;
        }

        public static Border SqlTag(string text)
        {
            return new Border
            {
                Height = 16,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 0, 5, 0),
                BorderBrush = Theme.SqlTag,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Theme.SqlTag
                }
            };
        }

        public static TextBlock BindNameText(string name)
        {
            var tb = new TextBlock
            {
                Text = name,
                FontFamily = Theme.Mono,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.BindName,
                VerticalAlignment = VerticalAlignment.Center
            };
            return tb;
        }

        private static ControlTemplate PrimaryTemplate()
        {
            if (_primaryTemplate != null)
            {
                return _primaryTemplate;
            }

            var border = new FrameworkElementFactory(typeof(Border), "border");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetResourceReference(Border.BackgroundProperty, Theme.Accent);
            border.SetResourceReference(Border.BorderBrushProperty, Theme.Accent);
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            template.Seal();
            _primaryTemplate = template;
            return _primaryTemplate;
        }
    }
}
