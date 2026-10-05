using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MigrationStudio.Core.Settings;
using MigrationStudio.Logic;
using MigrationStudio.Services;

namespace MigrationStudio.Ui
{
    internal sealed class StepRail : Border
    {
        private readonly StudioState _state;
        private readonly Action<int> _onStep;
        private readonly StackPanel _stepsPanel = new StackPanel();
        private readonly TextBlock _jobTitle = new TextBlock();
        private readonly StackPanel _jobFlow = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly Border _headBorder = new Border();
        private readonly Border _footBorder = new Border();
        private bool _collapsed;

        public StepRail(StudioState state, Action<int> onStep)
        {
            _state = state;
            _onStep = onStep;
            MinWidth = 208;
            Width = 208;
            SetResourceReference(BackgroundProperty, Theme.PanelBackground);
            BorderThickness = new Thickness(0, 0, 1, 0);
            SetResourceReference(BorderBrushProperty, Theme.Border);

            var root = new DockPanel();
            _headBorder.Padding = new Thickness(12, 14, 12, 10);
            _headBorder.BorderThickness = new Thickness(0, 0, 0, 1);
            _headBorder.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            var head = new StackPanel();
            var cap = Theme.Secondary("이관 작업");
            cap.FontSize = 11;
            head.Children.Add(cap);
            _jobTitle.FontWeight = FontWeights.SemiBold;
            _jobTitle.FontSize = 13.5;
            _jobTitle.TextTrimming = TextTrimming.CharacterEllipsis;
            head.Children.Add(_jobTitle);
            _jobFlow.Margin = new Thickness(0, 4, 0, 0);
            head.Children.Add(_jobFlow);
            _headBorder.Child = head;
            DockPanel.SetDock(_headBorder, Dock.Top);
            root.Children.Add(_headBorder);

            _stepsPanel.Margin = new Thickness(10, 8, 10, 8);
            root.Children.Add(_stepsPanel);

            _footBorder.Padding = new Thickness(12, 10, 12, 12);
            _footBorder.BorderThickness = new Thickness(0, 1, 0, 0);
            _footBorder.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            var foot = new StackPanel();
            foot.Children.Add(LinkRow(Icons.Page, "작업 정의", () => { }));
            foot.Children.Add(LinkRow(Icons.ExportFile, "매핑 템플릿", null, false));
            _footBorder.Child = foot;
            DockPanel.SetDock(_footBorder, Dock.Bottom);
            root.Children.Add(_footBorder);

            Child = root;
            Loaded += OnLoaded;
            RebuildSteps();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow(this);
            if (window != null)
            {
                window.SizeChanged += OnWindowSizeChanged;
                ApplyCollapse(window.ActualWidth < 1000);
            }
        }

        private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyCollapse(e.NewSize.Width < 1000);
        }

        public void Refresh()
        {
            var job = _state.Job;
            _jobTitle.Text = (job != null ? job.JobName : "") + (_state.Dirty ? " *" : "");
            if (job != null && !string.IsNullOrEmpty(job.Description))
            {
                _jobTitle.ToolTip = job.Description;
            }

            _jobFlow.Children.Clear();
            _jobFlow.Children.Add(Kit.DbBadge(_state.ProfileForRole(Roles.Source)));
            _jobFlow.Children.Add(new TextBlock
            {
                Text = Icons.Arrow,
                FontFamily = Theme.IconFont,
                FontSize = 10,
                Margin = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            _jobFlow.Children.Add(Kit.DbBadge(_state.ProfileForRole(Roles.Target)));
            RebuildSteps();
        }

        private Action _jobDefinitionAction;
        private Action _templateExportAction;

        public void SetFootJobDefinitionAction(Action action)
        {
            _jobDefinitionAction = action;
            RebuildFoot();
        }

        public void SetFootTemplateExportAction(Action action)
        {
            _templateExportAction = action;
            RebuildFoot();
        }

        private void RebuildFoot()
        {
            var foot = new StackPanel();
            foot.Children.Add(LinkRow(Icons.Page, "작업 정의", _jobDefinitionAction, _jobDefinitionAction != null));
            foot.Children.Add(LinkRow(Icons.ExportFile, "매핑 템플릿", _templateExportAction, _templateExportAction != null));
            _footBorder.Child = foot;
        }

        private void ApplyCollapse(bool collapse)
        {
            if (collapse == _collapsed)
            {
                return;
            }

            _collapsed = collapse;
            Width = collapse ? 52 : 208;
            MinWidth = collapse ? 52 : 208;
            _headBorder.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
            _footBorder.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
            RebuildSteps();
        }

        private void RebuildSteps()
        {
            _stepsPanel.Children.Clear();
            for (var i = 0; i < Labels.StepTitles.Length; i++)
            {
                _stepsPanel.Children.Add(BuildStepRow(i));
            }
        }

        private RunSummaryInput RunSummary()
        {
            var run = _state.Run;
            if (run == null)
            {
                return null;
            }

            var snap = run.Snapshot;
            return new RunSummaryInput
            {
                State = run.State,
                Dry = run.Dry,
                Pct = snap != null && snap.Totals != null ? snap.Totals.Pct : 0,
                Elapsed = snap != null ? snap.Elapsed : 0
            };
        }

        private PreSessionSummary PreSummary()
        {
            var pre = _state.Pre;
            if (pre == null)
            {
                return null;
            }

            var counts = ValidationLogic.Count(pre.Items);
            return new PreSessionSummary
            {
                Exists = true,
                Running = pre.Running,
                ItemCount = pre.Items.Count,
                Errors = counts.Error,
                Warnings = counts.Warn,
                Passes = counts.Pass,
                Stale = !pre.Running && pre.JobVersion != _state.JobVersion
            };
        }

        private UIElement BuildStepRow(int index)
        {
            var sessionSrc = ToSession(_state.Conn[Roles.Source]);
            var sessionTgt = ToSession(_state.Conn[Roles.Target]);
            var metaSrc = _state.Conn[Roles.Source].Metadata;
            var metaTgt = _state.Conn[Roles.Target].Metadata;
            var info = StepLogic.ForStep(index, _state.Job, _state.Settings, sessionSrc, sessionTgt, metaSrc, metaTgt, PreSummary(), RunSummary());
            var current = _state.CurrentStep == index;
            var row = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 8, 7, 8),
                Margin = new Thickness(0, 0, 0, 2),
                Cursor = Cursors.Hand,
                Focusable = true
            };
            if (current)
            {
                row.SetResourceReference(Border.BackgroundProperty, Theme.Selection);
                row.BorderThickness = new Thickness(2, 0, 0, 0);
                row.SetResourceReference(Border.BorderBrushProperty, Theme.Accent);
            }

            row.MouseEnter += (s, e) => row.SetResourceReference(Border.BackgroundProperty, Theme.RowHover);
            row.MouseLeave += (s, e) =>
            {
                if (current)
                {
                    row.SetResourceReference(Border.BackgroundProperty, Theme.Selection);
                }
                else
                {
                    row.ClearValue(Border.BackgroundProperty);
                }
            };
            row.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Space)
                {
                    _onStep(index);
                    e.Handled = true;
                }
            };
            row.MouseLeftButtonUp += (s, e) => _onStep(index);

            var inner = new StackPanel { Orientation = Orientation.Horizontal };
            inner.Children.Add(StepCircle(index + 1, info.State, current));
            if (!_collapsed)
            {
                var texts = new StackPanel { Margin = new Thickness(9, 0, 0, 0) };
                var title = Theme.Text(Labels.StepTitles[index]);
                title.FontWeight = FontWeights.SemiBold;
                texts.Children.Add(title);
                var sub = Theme.Secondary(info.Summary ?? "");
                sub.TextTrimming = TextTrimming.CharacterEllipsis;
                texts.Children.Add(sub);
                inner.Children.Add(texts);
            }
            else
            {
                row.ToolTip = Labels.StepTitles[index] + " (Ctrl+" + (index + 1) + ") — " + (info.Summary ?? "");
            }

            row.Child = inner;
            return row;
        }

        private static ConnectionSession ToSession(ConnectionRoleState state)
        {
            if (state == null)
            {
                return null;
            }

            return new ConnectionSession
            {
                Status = state.Status,
                MetaLoading = state.MetaLoading,
                MetaError = state.MetaError
            };
        }

        private static FrameworkElement StepCircle(int number, string state, bool current)
        {
            var circle = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            circle.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            circle.SetResourceReference(Border.BackgroundProperty, Theme.ControlBackground);

            string glyph = number.ToString();
            if (state == "done")
            {
                glyph = Icons.Check;
                circle.BorderBrush = Theme.Success;
                circle.Background = Theme.SuccessTint;
            }
            else if (state == "error")
            {
                glyph = "!";
                circle.BorderBrush = Theme.Danger;
            }
            else if (state == "warn")
            {
                circle.BorderBrush = Theme.Warning;
                circle.Background = Theme.WarningTint;
            }
            else if (state == "busy")
            {
                circle.SetResourceReference(Border.BorderBrushProperty, Theme.Accent);
                return Kit.Spinner();
            }

            if (current)
            {
                circle.SetResourceReference(Border.BackgroundProperty, Theme.Accent);
                if (state == "done")
                {
                    circle.Background = Theme.Success;
                }
            }

            var text = new TextBlock
            {
                Text = glyph,
                FontSize = state == "done" ? 10 : 11.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (state == "done")
            {
                text.FontFamily = Theme.IconFont;
            }
            else if (state == "warn")
            {
                text.Foreground = Theme.Warning;
            }

            if (current)
            {
                text.SetResourceReference(TextBlock.ForegroundProperty, Theme.PanelBackground);
            }
            else
            {
                text.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
            }

            circle.Child = text;
            return circle;
        }

        private static UIElement LinkRow(string icon, string text, Action action, bool enabled)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = icon, FontFamily = Theme.IconFont, Margin = new Thickness(0, 0, 6, 0) });
            if (enabled && action != null)
            {
                row.Children.Add(Kit.LinkButton(text, action));
            }
            else
            {
                var disabled = Theme.Secondary(text);
                disabled.IsEnabled = false;
                disabled.Opacity = 0.5;
                row.Children.Add(disabled);
            }

            return row;
        }

        private static UIElement LinkRow(string icon, string text, Action action)
        {
            return LinkRow(icon, text, action, true);
        }
    }
}
