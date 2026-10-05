using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace DevHost
{
    internal sealed class LayoutIssue
    {
        public string Code { get; set; }
        public string Element { get; set; }
        public string Path { get; set; }
        public string Detail { get; set; }
    }

    internal static class LayoutChecker
    {
        private const double Tolerance = 1.0;
        private const double OverlapLimit = 5.0;

        public static IList<LayoutIssue> Check(Window window, bool dump)
        {
            var issues = new List<LayoutIssue>();
            var root = window.Content as FrameworkElement;
            if (root == null)
            {
                Add(issues, "ZeroSized", window, "Window", "창 콘텐츠가 없습니다");
                return issues;
            }

            CheckWindowTheme(window, issues);
            Walk(root, root, "Window/" + NameOf(root), issues, dump);
            return issues;
        }

        public static void WriteJson(IList<LayoutIssue> issues)
        {
            foreach (var issue in issues)
            {
                Console.WriteLine(JsonSerializer.Serialize(issue));
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                type = "summary",
                issues = issues.Count,
                byCode = issues.GroupBy(x => x.Code).OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Count())
            }));
        }

        public static int SelfTest()
        {
            var window = new Window
            {
                Width = 320,
                Height = 260,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = Brushes.White,
                Foreground = Brushes.Black
            };
            var root = new StackPanel { Width = 300 };
            var clip = new Canvas { Width = 100, Height = 30, ClipToBounds = true };
            var outside = new Border { Width = 30, Height = 20 };
            Canvas.SetLeft(outside, 90);
            Canvas.SetTop(outside, 20);
            clip.Children.Add(outside);
            root.Children.Add(clip);
            var textClip = new Grid { Width = 20, HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true };
            var clippedText = new TextBlock
            {
                Text = "THIS TEXT MUST BE CLIPPED BY THE SELF TEST",
                Width = 20,
                MaxWidth = 20,
                HorizontalAlignment = HorizontalAlignment.Left,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.None
            };
            textClip.Children.Add(clippedText);
            root.Children.Add(textClip);
            root.Children.Add(new TextBlock { Text = "\uE713", FontFamily = new FontFamily("Arial") });
            root.Children.Add(new ComboBox());
            var overlap = new Grid { Width = 100, Height = 20 };
            overlap.Children.Add(new Border { Width = 40, Height = 20, HorizontalAlignment = HorizontalAlignment.Left });
            overlap.Children.Add(new Border { Width = 40, Height = 20, HorizontalAlignment = HorizontalAlignment.Left });
            root.Children.Add(overlap);
            root.Children.Add(new Border { Tag = "Card", Width = 0, Height = 0 });
            window.Content = root;
            window.Show();
            window.UpdateLayout();
            clippedText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            clippedText.Arrange(new Rect(0, 0, 20, clippedText.DesiredSize.Height));
            var issues = Check(window, false);
            window.Close();

            var expected = new[] { "ClippedRight", "ClippedBottom", "TextClipped", "IconBox", "EmptyChoice", "Overlap", "ZeroSized", "Unthemed" };
            var missing = expected.Where(code => !issues.Any(x => x.Code == code)).ToList();
            WriteJson(issues);
            Console.WriteLine(JsonSerializer.Serialize(new { type = "selftest", ok = missing.Count == 0, missing }));
            return missing.Count == 0 ? 0 : 1;
        }

        private static void Walk(FrameworkElement element, FrameworkElement root, string path, List<LayoutIssue> issues, bool dump)
        {
            if (dump)
            {
                var bounds = BoundsIn(element, root);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    type = "element",
                    path,
                    element = NameOf(element),
                    x = Math.Round(bounds.X, 1),
                    y = Math.Round(bounds.Y, 1),
                    width = Math.Round(element.ActualWidth, 1),
                    height = Math.Round(element.ActualHeight, 1),
                    text = TextOf(element)
                }));
            }

            if (element.IsVisible)
            {
                CheckRequiredSize(element, path, issues);
                if (element.ActualWidth > 0 && element.ActualHeight > 0)
                {
                    CheckClipping(element, root, path, issues);
                    CheckText(element, path, issues);
                    CheckIcon(element, path, issues);
                    CheckChoice(element, path, issues);
                    CheckOverlap(element, path, issues);
                }
            }

            var count = VisualTreeHelper.GetChildrenCount(element);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(element, i) as FrameworkElement;
                if (child != null)
                {
                    Walk(child, root, path + "/" + NameOf(child) + "[" + i + "]", issues, dump);
                }
            }
        }

        private static void CheckClipping(FrameworkElement element, FrameworkElement root, string path, List<LayoutIssue> issues)
        {
            var ancestor = VisualTreeHelper.GetParent(element) as FrameworkElement;
            while (ancestor != null && ancestor != root && !(ancestor is ScrollViewer) && !ancestor.ClipToBounds)
            {
                ancestor = VisualTreeHelper.GetParent(ancestor) as FrameworkElement;
            }

            if (ancestor == null)
            {
                ancestor = root;
            }

            var bounds = BoundsIn(element, ancestor);
            if (bounds.IsEmpty)
            {
                return;
            }

            var scroll = ancestor as ScrollViewer;
            var allowHorizontal = scroll != null && string.Equals(scroll.Tag as string, "AllowHScroll", StringComparison.Ordinal);
            // Auto·Visible은 사용자가 스크롤하고, Hidden은 스크롤바만 숨긴 채 코드가 스크롤하는 것(줄 번호 열)이라 세로 넘침을 허용한다. Disabled만 막는다.
            var allowVertical = scroll != null && scroll.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled;
            if (!allowHorizontal && bounds.Right > ancestor.ActualWidth + Tolerance)
            {
                Add(issues, "ClippedRight", element, path, FormatBounds(bounds, ancestor));
            }

            if (!allowVertical && bounds.Bottom > ancestor.ActualHeight + Tolerance)
            {
                Add(issues, "ClippedBottom", element, path, FormatBounds(bounds, ancestor));
            }
        }

        private static void CheckText(FrameworkElement element, string path, List<LayoutIssue> issues)
        {
            var text = element as TextBlock;
            if (text != null && text.TextTrimming == TextTrimming.None && text.TextWrapping == TextWrapping.NoWrap && !string.IsNullOrEmpty(text.Text))
            {
                var desired = Math.Max(0, text.DesiredSize.Width - text.Margin.Left - text.Margin.Right);
                if (text.ActualWidth + Tolerance < desired || IsOutsideNearestClip(text))
                {
                    Add(issues, "TextClipped", element, path, "actual=" + Round(text.ActualWidth) + ", desired=" + Round(desired) + ", text=" + text.Text);
                }
            }

            var presenter = element as ContentPresenter;
            if (presenter != null && presenter.Content is string)
            {
                // DesiredSize에는 바깥 여백(Folderss ComboBox는 Padding을 ContentPresenter 여백으로 씀)이 들어 있지만 ActualWidth에는 없다
                var desired = Math.Max(0, presenter.DesiredSize.Width - presenter.Margin.Left - presenter.Margin.Right);
                var control = presenter.TemplatedParent as Control;
                if (control != null)
                {
                    desired = Math.Max(0, desired - control.Padding.Left - control.Padding.Right);
                }
                if (presenter.ActualWidth + Tolerance < desired)
                {
                    Add(issues, "TextClipped", element, path, "actual=" + Round(presenter.ActualWidth) + ", desired=" + Round(desired) + ", text=" + presenter.Content);
                }
            }
        }

        private static bool IsOutsideNearestClip(FrameworkElement element)
        {
            var ancestor = VisualTreeHelper.GetParent(element) as FrameworkElement;
            while (ancestor != null)
            {
                if (ancestor is ScrollViewer || ancestor.ClipToBounds)
                {
                    var bounds = BoundsIn(element, ancestor);
                    return bounds.Right > ancestor.ActualWidth + Tolerance;
                }

                ancestor = VisualTreeHelper.GetParent(ancestor) as FrameworkElement;
            }

            return false;
        }

        private static void CheckIcon(FrameworkElement element, string path, List<LayoutIssue> issues)
        {
            var text = element as TextBlock;
            if (text == null || string.IsNullOrEmpty(text.Text) || !text.Text.Any(c => c >= '\uE000' && c <= '\uF8FF'))
            {
                return;
            }

            var family = text.FontFamily != null ? text.FontFamily.Source : "";
            if (family.IndexOf("Segoe Fluent Icons", StringComparison.OrdinalIgnoreCase) < 0 && family.IndexOf("Segoe MDL2 Assets", StringComparison.OrdinalIgnoreCase) < 0)
            {
                Add(issues, "IconBox", element, path, "font=" + family + ", text=" + text.Text);
            }
        }

        private static void CheckChoice(FrameworkElement element, string path, List<LayoutIssue> issues)
        {
            var combo = element as ComboBox;
            if (combo != null && !combo.IsEditable && combo.Items.Count == 0 && !string.Equals(combo.Tag as string, "optional", StringComparison.Ordinal))
            {
                Add(issues, "EmptyChoice", element, path, "선택 항목이 없습니다");
            }
        }

        private static void CheckOverlap(FrameworkElement parent, string path, List<LayoutIssue> issues)
        {
            if (!(parent is Grid) && !(parent is StackPanel) && !(parent is WrapPanel))
            {
                return;
            }

            if (parent.TemplatedParent != null)
            {
                return;
            }

            var children = new List<FrameworkElement>();
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i) as FrameworkElement;
                if (child != null && child.IsVisible && child.ActualWidth > 0 && child.ActualHeight > 0 && !string.Equals(child.Tag as string, "overlay", StringComparison.Ordinal))
                {
                    children.Add(child);
                }
            }

            for (var i = 0; i < children.Count; i++)
            {
                var a = BoundsIn(children[i], parent);
                for (var j = i + 1; j < children.Count; j++)
                {
                    var b = BoundsIn(children[j], parent);
                    var intersection = Rect.Intersect(a, b);
                    if (!intersection.IsEmpty && intersection.Width > OverlapLimit && intersection.Height > OverlapLimit)
                    {
                        Add(issues, "Overlap", children[j], path, NameOf(children[i]) + " / " + NameOf(children[j]) + " overlap=" + Round(intersection.Width) + "x" + Round(intersection.Height));
                    }
                }
            }
        }

        private static void CheckRequiredSize(FrameworkElement element, string path, List<LayoutIssue> issues)
        {
            var tag = element.Tag as string;
            if ((tag == "Card" || tag == "RowGrid" || tag == "PageFrameBody") && (element.ActualWidth <= 0 || element.ActualHeight <= 0))
            {
                Add(issues, "ZeroSized", element, path, "size=" + Round(element.ActualWidth) + "x" + Round(element.ActualHeight));
            }
        }

        private static void CheckWindowTheme(Window window, List<LayoutIssue> issues)
        {
            var bg = DependencyPropertyHelper.GetValueSource(window, Window.BackgroundProperty);
            var fg = DependencyPropertyHelper.GetValueSource(window, Window.ForegroundProperty);
            if (window.Background == null || window.Background == Brushes.Transparent || !bg.IsExpression)
            {
                Add(issues, "Unthemed", window, "Window", "Background가 테마 리소스가 아닙니다");
            }

            if (window.Foreground == null || !fg.IsExpression)
            {
                Add(issues, "Unthemed", window, "Window", "Foreground가 테마 리소스가 아닙니다");
            }
        }

        private static Rect BoundsIn(FrameworkElement element, FrameworkElement ancestor)
        {
            try
            {
                return element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                return Rect.Empty;
            }
        }

        private static string TextOf(FrameworkElement element)
        {
            if (element is TextBlock)
            {
                return ((TextBlock)element).Text;
            }

            if (element is ContentPresenter && ((ContentPresenter)element).Content is string)
            {
                return (string)((ContentPresenter)element).Content;
            }

            return null;
        }

        private static string NameOf(FrameworkElement element)
        {
            return element.GetType().Name + (string.IsNullOrEmpty(element.Name) ? "" : "#" + element.Name);
        }

        private static string FormatBounds(Rect bounds, FrameworkElement ancestor)
        {
            return "bounds=" + Round(bounds.X) + "," + Round(bounds.Y) + "," + Round(bounds.Width) + "," + Round(bounds.Height) + "; clip=" + Round(ancestor.ActualWidth) + "x" + Round(ancestor.ActualHeight);
        }

        private static string Round(double value)
        {
            return Math.Round(value, 1).ToString(CultureInfo.InvariantCulture);
        }

        private static void Add(List<LayoutIssue> issues, string code, FrameworkElement element, string path, string detail)
        {
            issues.Add(new LayoutIssue { Code = code, Element = NameOf(element), Path = path, Detail = detail });
        }
    }
}
