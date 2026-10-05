using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MigrationStudio.Core.Settings;
using MigrationStudio.Logic;

namespace MigrationStudio.Ui
{
    internal enum SaveChoice
    {
        Save,
        Discard,
        Cancel
    }

    internal static class Dialogs
    {
        public static SaveChoice AskSaveChanges(Window owner, string title, string filePath)
        {
            var window = DialogKit.Create(owner, "저장하지 않은 변경", 440);
            var body = DialogKit.Body(window);
            var what = filePath != null
                ? "'" + title + "'에 저장하지 않은 변경이 있습니다." + Environment.NewLine + filePath
                : "'" + title + "'에 저장하지 않은 변경이 있습니다.";
            body.Children.Add(DialogKit.Text(what + Environment.NewLine + Environment.NewLine + "저장할까요?", 0));
            var result = SaveChoice.Cancel;
            var save = DialogKit.PrimaryButton("저장");
            var discard = DialogKit.PlainButton("저장 안 함");
            discard.Margin = new Thickness(8, 0, 0, 0);
            var cancel = DialogKit.CancelButton("취소", true);
            cancel.Margin = new Thickness(8, 0, 0, 0);
            save.Click += (s, e) =>
            {
                result = SaveChoice.Save;
                window.DialogResult = true;
            };
            discard.Click += (s, e) =>
            {
                result = SaveChoice.Discard;
                window.DialogResult = true;
            };
            body.Children.Add(DialogKit.Buttons(save, discard, cancel));
            DialogKit.FocusOnLoad(window, cancel);
            return window.ShowDialog() == true ? result : SaveChoice.Cancel;
        }

        public static void Show(Window owner, string title, string message, bool error)
        {
            var window = DialogKit.Create(owner, string.IsNullOrWhiteSpace(title) ? "Migration Studio" : title, 440);
            var body = DialogKit.Body(window);
            var row = new DockPanel();
            var icon = DialogKit.Icon(error);
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var text = new TextBlock { Text = message ?? "", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            if (error)
            {
                text.Foreground = Theme.Danger;
            }
            else
            {
                Theme.Foreground(text);
            }

            row.Children.Add(new ScrollViewer
            {
                Content = text,
                MaxHeight = 360,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false
            });
            body.Children.Add(row);

            var ok = DialogKit.CancelButton("확인", true);
            body.Children.Add(DialogKit.Buttons(ok));
            window.KeyDown += (s, e) =>
            {
                if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    DialogKit.TryCopy(window.Title + Environment.NewLine + (message ?? ""));
                    e.Handled = true;
                }
            };
            DialogKit.FocusOnLoad(window, ok);
            window.ShowDialog();
        }

        public static string PasswordPrompt(Window owner, ConnectionProfile profile, string reason, bool rememberDefault)
        {
            var window = DialogKit.Create(owner, DialogKit.TitleFor("비밀번호 입력", profile), 420);
            var body = DialogKit.Body(window);
            body.Children.Add(DialogKit.Target(profile, ConnectionLogic.Address(profile)));
            body.Children.Add(DialogKit.Hint(string.IsNullOrWhiteSpace(reason) ? "입력한 비밀번호는 저장하지 않고 이번 연결에만 씁니다." : reason.Trim(), 8));
            var box = DialogKit.PasswordInput();
            box.Margin = new Thickness(0, 10, 0, 0);
            AutomationProperties.SetName(box, "비밀번호");
            body.Children.Add(box);
            var remember = DialogKit.Check("이번 창에서만 기억");
            remember.IsChecked = rememberDefault;
            remember.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(remember);

            var connect = DialogKit.PrimaryButton("연결");
            connect.IsDefault = true;
            connect.IsEnabled = false;
            var cancel = DialogKit.CancelButton("취소", false);
            box.PasswordChanged += (s, e) => connect.IsEnabled = box.Password.Length > 0;
            string password = null;
            connect.Click += (s, e) =>
            {
                if (box.Password.Length > 0)
                {
                    password = box.Password;
                    window.Tag = remember.IsChecked == true;
                    window.DialogResult = true;
                }
            };
            body.Children.Add(DialogKit.Buttons(connect, cancel));
            DialogKit.FocusOnLoad(window, box);
            var ok = window.ShowDialog() == true;
            var result = ok ? password : null;
            box.Clear();
            return string.IsNullOrEmpty(result) ? null : result;
        }

        public static bool PasswordRememberChoice(Window promptWindow)
        {
            return promptWindow != null && promptWindow.Tag is bool && (bool)promptWindow.Tag;
        }

        public static bool ConfirmDanger(Window owner, string title, string message)
        {
            var window = DialogKit.Create(owner, title, 440);
            var body = DialogKit.Body(window);
            body.Children.Add(DialogKit.Text(message, 0));
            var ok = DialogKit.PrimaryButton("삭제");
            ok.Foreground = Theme.Danger;
            var cancel = DialogKit.CancelButton("취소", true);
            var result = false;
            ok.Click += (s, e) =>
            {
                result = true;
                window.DialogResult = true;
            };
            body.Children.Add(DialogKit.Buttons(cancel, ok));
            window.ShowDialog();
            return result;
        }
    }

    internal static class DialogKit
    {
        public static Window Create(Window owner, string title, double width)
        {
            var window = new Window
            {
                Title = title,
                Width = width,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };
            Theme.ApplyWindow(window);
            SetOwner(window, owner);
            return window;
        }

        public static void SetOwner(Window window, Window owner)
        {
            var target = owner ?? ActiveWindow();
            if (target != null && target != window && target.CheckAccess() && new WindowInteropHelper(target).Handle != IntPtr.Zero)
            {
                window.Owner = target;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        public static string TitleFor(string title, ConnectionProfile profile)
        {
            return profile == null || string.IsNullOrWhiteSpace(profile.Name) ? title : title + " · " + profile.Name.Trim();
        }

        public static StackPanel Body(Window window)
        {
            var body = new StackPanel { Margin = new Thickness(16) };
            window.Content = body;
            return body;
        }

        public static TextBlock Text(string text, double top)
        {
            return Theme.Foreground(new TextBlock { Text = text ?? "", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) });
        }

        public static TextBlock Hint(string text, double top)
        {
            var block = Theme.Secondary(text ?? "");
            block.TextWrapping = TextWrapping.Wrap;
            block.FontSize = 12;
            block.Margin = new Thickness(0, top, 0, 0);
            return block;
        }

        public static FrameworkElement Target(ConnectionProfile profile, string text)
        {
            var row = new DockPanel();
            var badge = Theme.DbBadge(profile);
            badge.VerticalAlignment = VerticalAlignment.Top;
            badge.Margin = new Thickness(0, 1, 8, 0);
            DockPanel.SetDock(badge, Dock.Left);
            row.Children.Add(badge);
            row.Children.Add(Theme.Foreground(new TextBlock { Text = text ?? "", TextWrapping = TextWrapping.Wrap }));
            return row;
        }

        public static CheckBox Check(string text)
        {
            return Theme.Foreground(new CheckBox { Content = text, Margin = new Thickness(0, 12, 0, 0) });
        }

        public static Button PrimaryButton(string text)
        {
            var button = new Button { Content = text, MinWidth = 80, FontWeight = FontWeights.SemiBold };
            button.SetResourceReference(Control.BorderBrushProperty, Theme.Accent);
            return button;
        }

        public static Button PlainButton(string text)
        {
            return new Button { Content = text, MinWidth = 80 };
        }

        public static Button CancelButton(string text, bool isDefault)
        {
            var button = PlainButton(text);
            button.IsCancel = true;
            button.IsDefault = isDefault;
            return button;
        }

        public static FrameworkElement Buttons(params Button[] buttons)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 13, 0, 0) };
            foreach (var button in buttons)
            {
                panel.Children.Add(button);
            }

            return panel;
        }

        public static PasswordBox PasswordInput()
        {
            var box = new PasswordBox
            {
                Padding = new Thickness(8, 5, 8, 5),
                BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            box.SetResourceReference(Control.BackgroundProperty, Theme.ControlBackground);
            box.SetResourceReference(Control.ForegroundProperty, Theme.PrimaryText);
            box.SetResourceReference(Control.BorderBrushProperty, Theme.Border);
            box.SetResourceReference(PasswordBox.CaretBrushProperty, Theme.PrimaryText);
            box.SetResourceReference(PasswordBox.SelectionBrushProperty, Theme.Selection);
            return box;
        }

        public static FrameworkElement Icon(bool error)
        {
            var glyph = new TextBlock
            {
                Text = error ? "!" : "i",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var circle = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = glyph
            };
            if (error)
            {
                circle.Background = Theme.Danger;
            }
            else
            {
                circle.SetResourceReference(Border.BackgroundProperty, Theme.Accent);
            }

            return circle;
        }

        public static void FocusOnLoad(Window window, IInputElement element)
        {
            window.Loaded += (s, e) => element.Focus();
        }

        public static bool TryCopy(string text)
        {
            try
            {
                Clipboard.SetText(text ?? "");
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Window ActiveWindow()
        {
            var app = Application.Current;
            if (app == null || !app.CheckAccess())
            {
                return null;
            }

            foreach (Window window in app.Windows)
            {
                if (window.IsActive)
                {
                    return window;
                }
            }

            return app.MainWindow;
        }
    }
}
