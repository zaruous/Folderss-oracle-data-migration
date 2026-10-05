using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using MigrationStudio.Logic;

namespace MigrationStudio.Ui
{
    internal interface IShellCommands
    {
        void NewJob();
        void OpenJob();
        void LoadSampleJob();
        void SaveJob();
        void SaveYaml();
        void ShowJobDefinition();
        void ImportTemplate();
        void ExportTemplate();
        void OpenSettings(string tab);
        void TestAllConnections();
        void ReloadAllMetadata();
        void AutoMatchTables();
        void AutoMapColumns();
        void OpenSqlEditor();
        void AddSqlSource();
        void ValidateSql();
        void PreviewSql();
        void RunValidation();
        void RunPostValidation();
        void DryRun();
        void StartRun();
        void PauseRun();
        void ResumeRun();
        void StopRun();
        void ResumeFromCheckpoint();
        void GoToStep(int step);
        int CurrentStep { get; }
        void ShowShortcuts();
        void ShowAbout();
        ShellCommandState CommandState();
    }

    internal sealed class ShellCommandState
    {
        public bool CanTestConnections { get; set; } = true;
        public bool CanReloadMeta { get; set; }
        public bool CanValidation { get; set; }
        public bool CanStartRun { get; set; }
        public bool CanPause { get; set; }
        public bool CanStop { get; set; }
        public bool CanResume { get; set; }
        public bool CanResumeCheckpoint { get; set; }
        public bool CanPostValidation { get; set; }
        public bool CanSqlEditor { get; set; }
        public bool CanAutoMap { get; set; }
        public bool CanImportExport { get; set; }
    }

    internal sealed class ShellMenu
    {
        private readonly IShellCommands _commands;
        private readonly List<KeyValuePair<Button, Func<ShellCommandState, bool>>> _icons = new List<KeyValuePair<Button, Func<ShellCommandState, bool>>>();
        private readonly TextBlock _jobLabel;
        private MenuItem _importTemplateItem;
        private MenuItem _postValidateItem, _dryItem, _startItem, _pauseItem, _resumeItem, _stopItem, _resumeCheckpointItem;
        private MenuItem _exportTemplateItem;
        private MenuItem _autoMatchItem;
        private MenuItem _autoMapItem;
        private MenuItem _sqlEditorItem;
        private MenuItem _addSqlItem;
        private MenuItem _validateSqlItem;
        private MenuItem _previewSqlItem;

        public ShellMenu(IShellCommands commands)
        {
            _commands = commands ?? throw new ArgumentNullException(nameof(commands));
            var menu = new Menu { VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(2, 0, 2, 0) };
            menu.Resources.MergedDictionaries.Add(Resources());
            Theme.Background(menu, Theme.SurfaceBackground);
            Theme.Foreground(menu);
            menu.SetResourceReference(Control.FontFamilyProperty, Theme.AppFont);
            menu.Items.Add(FileMenu());
            menu.Items.Add(MappingMenu());
            menu.Items.Add(RunMenu());
            menu.Items.Add(ViewMenu());
            menu.Items.Add(HelpMenu());

            var menuBar = new Border { Child = menu, Padding = new Thickness(4, 1, 8, 1), BorderThickness = new Thickness(0, 0, 0, 1) };
            menuBar.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            Theme.Background(menuBar, Theme.SurfaceBackground);

            var icons = new DockPanel { LastChildFill = true, Margin = new Thickness(6, 0, 8, 0) };
            icons.Resources.MergedDictionaries.Add(Resources());
            var iconRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            iconRow.Children.Add(Icon(Icons.NewDoc, "새 작업", null, s => true, _commands.NewJob));
            iconRow.Children.Add(Icon(Icons.Open, "작업 열기 (Ctrl+O)", null, s => true, _commands.OpenJob));
            iconRow.Children.Add(Icon(Icons.Save, "작업 저장 (Ctrl+S)", null, s => true, _commands.SaveJob));
            iconRow.Children.Add(Divider());
            iconRow.Children.Add(Icon(Icons.Link, "두 접속 시험", null, s => s.CanTestConnections, _commands.TestAllConnections));
            iconRow.Children.Add(Icon(Icons.Sync, "메타데이터 다시 불러오기(원본·대상)", null, s => s.CanReloadMeta, _commands.ReloadAllMetadata));
            iconRow.Children.Add(Divider());
            iconRow.Children.Add(Icon(Icons.Checklist, "실행 전 검증 (F6)", null, s => s.CanValidation, _commands.RunValidation));
            iconRow.Children.Add(Icon(Icons.View, "Dry Run", null, s => s.CanStartRun, _commands.DryRun));
            iconRow.Children.Add(Divider());
            iconRow.Children.Add(Icon(Icons.Play, "이관 시작 (F5)", Theme.Success, s => s.CanStartRun || s.CanResume, () =>
            {
                if (_commands.CommandState().CanResume)
                {
                    _commands.ResumeRun();
                }
                else
                {
                    _commands.StartRun();
                }
            }));
            iconRow.Children.Add(Icon(Icons.Pause, "일시정지", Theme.Warning, s => s.CanPause, _commands.PauseRun));
            iconRow.Children.Add(Icon(Icons.Stop, "중지", Theme.Danger, s => s.CanStop, _commands.StopRun));
            iconRow.Children.Add(Icon(Icons.Resume, "체크포인트에서 재개", null, s => s.CanResumeCheckpoint, _commands.ResumeFromCheckpoint));
            iconRow.Children.Add(Divider());
            iconRow.Children.Add(Icon(Icons.Code, "SQL 원본 편집기 (Ctrl+Q)", null, s => s.CanSqlEditor, _commands.OpenSqlEditor));
            iconRow.Children.Add(Icon(Icons.Page, "작업 정의 보기", null, s => true, _commands.ShowJobDefinition));
            iconRow.Children.Add(Icon(Icons.Setting, "마이그레이션 설정", null, s => true, () => _commands.OpenSettings("connections")));
            DockPanel.SetDock(iconRow, Dock.Left);
            icons.Children.Add(iconRow);

            _jobLabel = Theme.Text("");
            _jobLabel.FontWeight = FontWeights.SemiBold;
            _jobLabel.HorizontalAlignment = HorizontalAlignment.Right;
            _jobLabel.VerticalAlignment = VerticalAlignment.Center;
            _jobLabel.Margin = new Thickness(8, 0, 0, 0);
            icons.Children.Add(_jobLabel);

            var iconBar = new Border { Child = icons, Padding = new Thickness(6, 2, 8, 2), BorderThickness = new Thickness(0, 0, 0, 1), MinHeight = 30 };
            iconBar.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            Theme.Background(iconBar, Theme.SurfaceBackground);

            var bars = new StackPanel();
            bars.Children.Add(menuBar);
            bars.Children.Add(iconBar);
            View = bars;
        }

        public FrameworkElement View { get; }

        public void RefreshJobTitle(string jobName, bool dirty)
        {
            _jobLabel.Text = "작업: " + (jobName ?? "") + (dirty ? " *" : "");
        }

        public void Refresh()
        {
            var state = SafeState();
            foreach (var pair in _icons)
            {
                pair.Key.IsEnabled = pair.Value(state);
            }

            SetEnabled(_importTemplateItem, state.CanImportExport);
            SetPlain(_dryItem, state.CanStartRun);
            SetPlain(_startItem, state.CanStartRun || state.CanResume);
            SetPlain(_pauseItem, state.CanPause);
            SetPlain(_resumeItem, state.CanResume);
            SetPlain(_stopItem, state.CanStop);
            SetPlain(_resumeCheckpointItem, state.CanResumeCheckpoint);
            SetPlain(_postValidateItem, state.CanPostValidation);
            SetEnabled(_exportTemplateItem, state.CanImportExport);
            SetEnabled(_autoMatchItem, true);
            SetEnabled(_autoMapItem, state.CanAutoMap);
            SetEnabled(_sqlEditorItem, state.CanSqlEditor);
            SetEnabled(_addSqlItem, true);
            SetEnabled(_validateSqlItem, state.CanSqlEditor);
            SetEnabled(_previewSqlItem, state.CanSqlEditor);
        }

        private static void SetPlain(MenuItem item, bool enabled)
        {
            if (item != null)
            {
                item.IsEnabled = enabled;
            }
        }

        private static void SetEnabled(MenuItem item, bool enabled)
        {
            if (item == null)
            {
                return;
            }

            item.IsEnabled = enabled;
            item.ToolTip = enabled ? null : Labels.NextPhaseTooltip;
        }

        private MenuItem FileMenu()
        {
            var top = Top("파일(_F)");
            top.Items.Add(Item("새 작업(_N)", "Ctrl+N", Icons.NewDoc, _commands.NewJob));
            top.Items.Add(Item("작업 열기(_O)…", "Ctrl+O", Icons.Open, _commands.OpenJob));
            top.Items.Add(Item("예제 작업 불러오기(_X)", null, null, _commands.LoadSampleJob));
            top.Items.Add(new Separator());
            top.Items.Add(Item("작업 저장(_S)", "Ctrl+S", Icons.Save, _commands.SaveJob));
            top.Items.Add(Item("YAML로 저장(_Y)", "Ctrl+Shift+S", Icons.SaveAs, _commands.SaveYaml));
            top.Items.Add(Item("작업 정의 보기(_J)…", null, Icons.Page, _commands.ShowJobDefinition));
            top.Items.Add(new Separator());
            _importTemplateItem = Item("매핑 템플릿 가져오기(_I)…", null, Icons.ImportFile, _commands.ImportTemplate);
            _exportTemplateItem = Item("매핑 템플릿 내보내기(_E)…", null, Icons.ExportFile, _commands.ExportTemplate);
            top.Items.Add(_importTemplateItem);
            top.Items.Add(_exportTemplateItem);
            top.Items.Add(new Separator());
            top.Items.Add(Item("마이그레이션 설정(_G)…", null, Icons.Setting, () => _commands.OpenSettings("connections")));
            return top;
        }

        private MenuItem MappingMenu()
        {
            var top = Top("매핑(_M)");
            _autoMatchItem = Item("테이블 자동 매칭(_A)", null, Icons.Magic, _commands.AutoMatchTables);
            _autoMapItem = Item("컬럼 자동 매핑(_C)", null, null, _commands.AutoMapColumns);
            top.Items.Add(_autoMatchItem);
            top.Items.Add(_autoMapItem);
            top.Items.Add(new Separator());
            _sqlEditorItem = Item("SQL 원본 편집기(_Q)…", "Ctrl+Q", Icons.Code, _commands.OpenSqlEditor);
            _addSqlItem = Item("SQL 원본 추가(_N)…", null, Icons.Add, _commands.AddSqlSource);
            _validateSqlItem = Item("SQL 검증(_V)", "Ctrl+Enter", Icons.Check, _commands.ValidateSql);
            _previewSqlItem = Item("SQL 100행 미리보기(_P)", null, Icons.View, _commands.PreviewSql);
            top.Items.Add(_sqlEditorItem);
            top.Items.Add(_addSqlItem);
            top.Items.Add(_validateSqlItem);
            top.Items.Add(_previewSqlItem);
            return top;
        }

        private MenuItem RunMenu()
        {
            var top = Top("실행(_R)");
            top.Items.Add(Item("실행 전 검증(_V)", "F6", Icons.Checklist, _commands.RunValidation));
            _postValidateItem = Item("실행 후 검증(_A)", null, null, _commands.RunPostValidation);
            top.Items.Add(_postValidateItem);
            top.Items.Add(new Separator());
            _dryItem = Item("Dry Run(_D)", null, Icons.View, _commands.DryRun);
            _startItem = Item("이관 시작(_S)", "F5", Icons.Play, _commands.StartRun);
            _pauseItem = Item("일시정지(_P)", null, Icons.Pause, _commands.PauseRun);
            _resumeItem = Item("이어서 실행(_C)", null, Icons.Play, _commands.ResumeRun);
            _stopItem = Item("중지(_T)", null, Icons.Stop, _commands.StopRun);
            _resumeCheckpointItem = Item("체크포인트에서 재개(_R)", null, Icons.Resume, _commands.ResumeFromCheckpoint);
            foreach (var runItem in new[] { _dryItem, _startItem, _pauseItem, _resumeItem, _stopItem, _resumeCheckpointItem })
            {
                top.Items.Add(runItem);
            }

            top.SubmenuOpened += (s, e) =>
            {
                if (ReferenceEquals(e.OriginalSource, top))
                {
                    Refresh();
                }
            };
            return top;
        }

        private MenuItem ViewMenu()
        {
            var top = Top("보기(_V)");
            for (var i = 0; i < Labels.StepTitles.Length; i++)
            {
                var step = i;
                var item = Item((i + 1) + " " + Labels.StepTitles[i], "Ctrl+" + (i + 1), null, () => _commands.GoToStep(step));
                top.Items.Add(item);
            }

            return top;
        }

        private MenuItem HelpMenu()
        {
            var top = Top("도움말(_H)");
            top.Items.Add(Item("단축키(_K)…", null, null, _commands.ShowShortcuts));
            top.Items.Add(Item("Migration Studio 정보(_A)…", null, Icons.Info, _commands.ShowAbout));
            return top;
        }

        private static MenuItem Top(string header)
        {
            return new MenuItem { Header = header };
        }

        private MenuItem Item(string header, string gesture, string icon, Action action)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", Tag = icon };
            item.Click += (s, e) => Run(action);
            return item;
        }

        private static MenuItem DisabledItem(string header, string icon, string gesture)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", Tag = icon, IsEnabled = false };
            item.ToolTip = Labels.NextPhaseTooltip;
            return item;
        }

        private static MenuItem DisabledItem(string header, string icon)
        {
            return DisabledItem(header, icon, null);
        }

        private Button Icon(string glyph, string tooltip, Brush color, Func<ShellCommandState, bool> enabled, Action action)
        {
            var button = new Button { Content = glyph, ToolTip = tooltip, Margin = new Thickness(1, 0, 1, 0) };
            button.SetResourceReference(FrameworkElement.StyleProperty, "ShellIconButton");
            if (color != null)
            {
                button.Foreground = color;
            }

            ToolTipService.SetShowOnDisabled(button, true);
            button.Click += (s, e) => Run(action);
            _icons.Add(new KeyValuePair<Button, Func<ShellCommandState, bool>>(button, enabled));
            return button;
        }

        private static FrameworkElement Divider()
        {
            var line = new Border { Width = 1, Height = 16, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            line.SetResourceReference(Border.BackgroundProperty, Theme.Border);
            return line;
        }

        private static void Run(Action action)
        {
            try
            {
                action();
            }
            catch (Exception)
            {
            }
        }

        private ShellCommandState SafeState()
        {
            try
            {
                return _commands.CommandState() ?? new ShellCommandState();
            }
            catch (Exception)
            {
                return new ShellCommandState();
            }
        }

        private static ResourceDictionary Resources()
        {
            return (ResourceDictionary)XamlReader.Parse(ResourcesXaml);
        }

        private const string ResourcesXaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='{x:Static MenuItem.SeparatorStyleKey}' TargetType='Separator'>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Separator'>
          <Border Height='1' Margin='8,3' Background='{DynamicResource BorderBrush}'/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='MenuItem'>
    <Setter Property='Foreground' Value='{DynamicResource PrimaryText}'/>
    <Setter Property='FontFamily' Value='{DynamicResource AppFontFamily}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='MenuItem'>
          <Border x:Name='Bd' Background='Transparent' Padding='8,4,10,4' SnapsToDevicePixels='True'>
            <Grid>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width='Auto' SharedSizeGroup='MenuIcon'/>
                <ColumnDefinition Width='*'/>
                <ColumnDefinition Width='Auto' SharedSizeGroup='MenuGesture'/>
                <ColumnDefinition Width='Auto'/>
              </Grid.ColumnDefinitions>
              <TextBlock x:Name='Glyph' Grid.Column='0' Width='16' Margin='0,0,8,0' VerticalAlignment='Center' TextAlignment='Center'
                         FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='12'
                         Text='{Binding Tag, RelativeSource={RelativeSource TemplatedParent}}'
                         Foreground='{DynamicResource SecondaryText}'/>
              <ContentPresenter x:Name='HeaderHost' Grid.Column='1' ContentSource='Header' RecognizesAccessKey='True' VerticalAlignment='Center'/>
              <TextBlock x:Name='Gesture' Grid.Column='2' Margin='28,0,0,0' VerticalAlignment='Center'
                         Text='{TemplateBinding InputGestureText}' Foreground='{DynamicResource SecondaryText}'/>
              <TextBlock x:Name='Arrow' Grid.Column='3' Margin='10,0,0,0' Text='›' VerticalAlignment='Center' Visibility='Collapsed'/>
              <Popup x:Name='PART_Popup' Placement='Right' HorizontalOffset='10' VerticalOffset='-5'
                     IsOpen='{Binding IsSubmenuOpen, RelativeSource={RelativeSource TemplatedParent}}'
                     AllowsTransparency='True' Focusable='False' PopupAnimation='None'>
                <Border Background='{DynamicResource SurfaceBackground}' BorderBrush='{DynamicResource BorderBrush}' BorderThickness='1' Padding='0,3' MinWidth='180'>
                  <ItemsPresenter KeyboardNavigation.DirectionalNavigation='Cycle' KeyboardNavigation.TabNavigation='Cycle' Grid.IsSharedSizeScope='True'/>
                </Border>
              </Popup>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='Role' Value='TopLevelHeader'>
              <Setter TargetName='Bd' Property='Padding' Value='9,3,9,3'/>
              <Setter TargetName='Glyph' Property='Visibility' Value='Collapsed'/>
              <Setter TargetName='Gesture' Property='Visibility' Value='Collapsed'/>
              <Setter TargetName='PART_Popup' Property='Placement' Value='Bottom'/>
              <Setter TargetName='PART_Popup' Property='HorizontalOffset' Value='0'/>
              <Setter TargetName='PART_Popup' Property='VerticalOffset' Value='1'/>
            </Trigger>
            <Trigger Property='IsHighlighted' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{DynamicResource ControlHoverBrush}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
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
    }
}
