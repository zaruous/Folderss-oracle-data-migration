using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using MigrationStudio;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Jobs;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using MigrationStudio.Core.Storage;
using MigrationStudio.Logic;
using MigrationStudio.Services;
using MigrationStudio.Testing.Adapters;
using MigrationStudio.Ui;
using MigrationStudio.Ui.Settings;

namespace DevHost
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            var theme = "black";
            var themesDir = @"D:\git\cshap\Folderss\Folderss\Themes";
            var dataDir = Path.Combine(Path.GetTempPath(), "migration-devhost-" + Guid.NewGuid().ToString("N"));
            var seed = false;
            var gallery = false;
            string shotPath = null;
            var size = "1100x700";
            var step = 1;
            string settingsTab = null;
            var fakeOracle = false;
            string openUi = null;
            var layoutCheck = false;
            var layoutSelfTest = false;
            var dump = false;
            var runValidation = false;
            string fakeRun = null;
            var scrollTo = 0;
            var realValidation = false;
            var freezeValidation = 0;
            var validationPass = false;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--theme":
                        theme = args[++i];
                        break;
                    case "--themes":
                        themesDir = args[++i];
                        break;
                    case "--data":
                        dataDir = args[++i];
                        break;
                    case "--seed":
                        seed = true;
                        break;
                    case "--gallery":
                        gallery = true;
                        break;
                    case "--shot":
                        shotPath = args[++i];
                        break;
                    case "--size":
                        size = args[++i];
                        break;
                    case "--step":
                        step = int.Parse(args[++i]);
                        break;
                    case "--settings":
                        settingsTab = args[++i];
                        break;
                    case "--fake-oracle":
                        fakeOracle = true;
                        break;
                    case "--open":
                        openUi = args[++i];
                        break;
                    case "--real-validation":
                        realValidation = true;
                        runValidation = true;
                        break;
                    case "--scroll":
                        scrollTo = int.Parse(args[++i]);
                        break;
                    case "--fake-run":
                        fakeRun = args[++i];
                        break;
                    case "--run-validation":
                        runValidation = true;
                        break;
                    case "--freeze-validation":
                        freezeValidation = int.Parse(args[++i]);
                        runValidation = true;
                        break;
                    case "--validation-pass":
                        validationPass = true;
                        runValidation = true;
                        break;
                    case "--layout-check":
                        layoutCheck = true;
                        break;
                    case "--layout-selftest":
                        layoutSelfTest = true;
                        break;
                    case "--dump":
                        dump = true;
                        break;
                }
            }

            var app = new Application();
            try
            {
                ThemeLoader.Apply(themesDir, theme);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }

            if (layoutSelfTest)
            {
                return LayoutChecker.SelfTest();
            }

            if (gallery)
            {
                var g = Gallery.Create();
                g.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
                g.SetResourceReference(Window.ForegroundProperty, "PrimaryText");
                g.SetResourceReference(Window.FontFamilyProperty, "AppFontFamily");
                g.FontSize = 13;
                if (!string.IsNullOrEmpty(shotPath))
                {
                    var parts = size.Split('x');
                    return Shot.Capture(g, shotPath, int.Parse(parts[0]), int.Parse(parts[1]));
                }

                g.ShowDialog();
                return 0;
            }

            var pluginDir = Path.GetDirectoryName(typeof(MigrationPlugin).Assembly.Location);
            var manager = new FakePluginManager(dataDir, pluginDir);
            if (seed)
            {
                SeedAll(manager);
            }

            AppServices.Manager = manager;
            AppServices.DataDirectory = manager.DataDirectory;
            AppServices.PluginDirectory = manager.PluginDirectory;
            AppServices.DevHostCaptureMode = !string.IsNullOrEmpty(shotPath) || layoutCheck;
            AppServices.DevHostShotWindow = null;
            if (fakeOracle)
            {
                ConfigureFakeOracle(manager);
                AppServices.DevHostPassword = "devhost";
                var runState = fakeRun;
                AppServices.RunServiceFactory = (state, connections) => new FakeRunService(runState);
                var freeze = freezeValidation;
                var passOnly = validationPass;
                if (!realValidation)
                {
                    AppServices.ValidationServiceFactory = (state, connections) => new FakeValidationService(freeze, freeze > 0 ? 15 : 0, passOnly);
                }
            }

            var plugin = new MigrationPlugin();
            var settingsPage = new MigrationSettingsPage();
            manager.RegisterSettingsPage(settingsPage);
            plugin.Initialize(manager);
            FrameworkElement view;
            Window window;
            if (!string.IsNullOrEmpty(settingsTab))
            {
                view = settingsPage.CreateView();
                window = new Window
                {
                    Title = "Migration Studio — 설정",
                    Content = view,
                    Width = 900,
                    Height = 620
                };
                if (view is MigrationSettingsView msv)
                {
                    msv.SelectTab(settingsTab);
                }
            }
            else
            {
                view = plugin.CreateView();
                window = new Window
                {
                    Title = "Migration Studio",
                    Content = view,
                    Width = 900,
                    Height = 600
                };
                if (view is MigrationView mv)
                {
                    // SQL 원본 편집기는 소유 창이 화면에 떠 있어야 열 수 있으므로 창을 띄운 뒤(OpenEditorAfterShow) 연다
                    mv.PrepareForDevHostShot(step, IsEditorOpen(openUi) ? null : openUi, fakeOracle);
                }
            }

            window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
            window.SetResourceReference(Window.ForegroundProperty, "PrimaryText");
            window.SetResourceReference(Window.FontFamilyProperty, "AppFontFamily");
            window.FontSize = 13;

            if (view is MigrationView mvRun && (!string.IsNullOrEmpty(fakeRun) || (openUi != null && openUi.StartsWith("run-popup", StringComparison.Ordinal))))
            {
                var rp = size.Split('x');
                PrepareWindow(window, int.Parse(rp[0]), int.Parse(rp[1]));
                System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                if (!string.IsNullOrEmpty(fakeRun))
                {
                    mvRun.AttachFakeRunForShot(s => new FakeAgentClient(s), fakeRun);
                }
                else
                {
                    mvRun.GoToStep(4);
                }

                if (openUi != null && openUi.StartsWith("run-popup", StringComparison.Ordinal))
                {
                    mvRun.ShowRunPopupForShot(openUi);
                }

                var settleRun = DateTime.UtcNow.AddMilliseconds(500);
                while (DateTime.UtcNow < settleRun)
                {
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                    System.Threading.Thread.Sleep(30);
                }
            }

            if (runValidation && view is MigrationView mvVal)
            {
                var vp = size.Split('x');
                RunValidationAfterShow(window, mvVal, freezeValidation, int.Parse(vp[0]), int.Parse(vp[1]));
            }

            if (scrollTo > 0)
            {
                ScrollPage(window, scrollTo);
            }

            if (layoutCheck)
            {
                var parts = size.Split('x');
                OpenEditorAfterShow(window, view, step, openUi, fakeOracle, int.Parse(parts[0]), int.Parse(parts[1]));
                var target = ResolveTargetWindow(window, view, openUi);
                PrepareWindow(target, int.Parse(parts[0]), int.Parse(parts[1]));
                var issues = LayoutChecker.Check(target, dump);
                LayoutChecker.WriteJson(issues);
                target.Close();
                return issues.Count == 0 ? 0 : 1;
            }

            if (!string.IsNullOrEmpty(shotPath))
            {
                var parts = size.Split('x');
                var w = int.Parse(parts[0]);
                var h = int.Parse(parts[1]);
                OpenEditorAfterShow(window, view, step, openUi, fakeOracle, w, h);
                if (view is MigrationView mvShot && !string.IsNullOrEmpty(openUi) && openUi.StartsWith("sql-editor", StringComparison.Ordinal))
                {
                    var editor = mvShot.SqlEditorWindowForShot;
                    if (editor != null)
                    {
                        return Shot.Capture(editor, shotPath, 1080, 720);
                    }
                }

                if (AppServices.DevHostShotWindow != null)
                {
                    return Shot.Capture(AppServices.DevHostShotWindow, shotPath, w, h, true);
                }

                return Shot.Capture(window, shotPath, w, h);
            }

            window.ShowDialog();
            return 0;
        }

        /// <summary>창을 띄운 뒤 실행 전 검증을 시작하고, 끝나거나(또는 freezeAt개를 받을 때까지) 기다린다.</summary>
        private static void RunValidationAfterShow(Window window, MigrationView view, int freezeAt, int width, int height)
        {
            PrepareWindow(window, width, height);
            // 디스패처를 직접 돌리지 않는 시험 환경이라 await 뒤가 UI 스레드로 돌아오도록 동기화 컨텍스트를 설치한다(실제 Folderss에서는 이미 있음)
            System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            view.RunValidation();
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                System.Threading.Thread.Sleep(30);
                if (freezeAt > 0 ? view.ValidationItemCountForShot >= freezeAt : !view.ValidationRunningForShot && view.ValidationItemCountForShot > 0)
                {
                    break;
                }
            }

            // 마지막 항목이 화면에 그려질 시간(다시 그리기는 0.12초에 한 번으로 묶인다)
            var settle = DateTime.UtcNow.AddMilliseconds(400);
            while (DateTime.UtcNow < settle)
            {
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                System.Threading.Thread.Sleep(30);
            }
        }

        /// <summary>화면 본문의 세로 스크롤을 지정한 위치(px)로 옮긴다(캡처용). 스크롤할 수 있는 첫 ScrollViewer를 쓴다.</summary>
        private static void ScrollPage(Window window, int offset)
        {
            window.Show();
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            var scroll = FindScroll(window);
            if (scroll != null)
            {
                scroll.ScrollToVerticalOffset(offset);
                scroll.UpdateLayout();
            }

            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private static System.Windows.Controls.ScrollViewer FindScroll(System.Windows.DependencyObject root)
        {
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                var sv = child as System.Windows.Controls.ScrollViewer;
                if (sv != null && sv.ScrollableHeight > 0 && sv.VerticalScrollBarVisibility == System.Windows.Controls.ScrollBarVisibility.Auto)
                {
                    return sv;
                }

                var found = FindScroll(child);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static bool IsEditorOpen(string openUi)
        {
            return !string.IsNullOrEmpty(openUi) && openUi.StartsWith("sql-editor", StringComparison.Ordinal);
        }

        /// <summary>메인 창을 먼저 화면에 띄워 Loaded(소유 창 설정)를 끝낸 뒤 SQL 원본 편집기를 연다.</summary>
        private static void OpenEditorAfterShow(Window window, FrameworkElement view, int step, string openUi, bool fakeOracle, int width, int height)
        {
            if (!IsEditorOpen(openUi) || !(view is MigrationView mv))
            {
                return;
            }

            PrepareWindow(window, width, height);
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            mv.PrepareForDevHostShot(step, openUi, fakeOracle);
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private static Window ResolveTargetWindow(Window owner, FrameworkElement view, string openUi)
        {
            if (view is MigrationView mv && !string.IsNullOrEmpty(openUi) && openUi.StartsWith("sql-editor", StringComparison.Ordinal))
            {
                var editor = mv.SqlEditorWindowForShot;
                if (editor != null)
                {
                    return editor;
                }
            }

            return AppServices.DevHostShotWindow ?? owner;
        }

        private static void PrepareWindow(Window window, int width, int height)
        {
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.ShowInTaskbar = false;
            window.Width = width;
            window.Height = height;
            if (!window.IsVisible)
            {
                window.Show();
            }

            window.UpdateLayout();
            var root = window.Content as FrameworkElement;
            if (root != null)
            {
                root.Measure(new Size(width, height));
                root.Arrange(new Rect(0, 0, width, height));
                root.UpdateLayout();
            }
        }

        private static void ConfigureFakeOracle(FakePluginManager manager)
        {
            var goldenPath = FindGoldenPath();
            SchemaMetadata source = null;
            SchemaMetadata target = null;
            if (!string.IsNullOrEmpty(goldenPath))
            {
                try
                {
                    using (var doc = JsonDocument.Parse(File.ReadAllText(goldenPath)))
                    {
                        if (doc.RootElement.TryGetProperty("source", out var sourceEl))
                        {
                            source = JsonSerializer.Deserialize<SchemaMetadata>(sourceEl.GetRawText(), MetadataJsonOptions.Options);
                        }

                        if (doc.RootElement.TryGetProperty("target", out var targetEl))
                        {
                            target = JsonSerializer.Deserialize<SchemaMetadata>(targetEl.GetRawText(), MetadataJsonOptions.Options);
                        }
                    }
                }
                catch (JsonException)
                {
                }
            }

            var adapter = new FakeAdapter(source ?? new SchemaMetadata());
            var responder = new SampleResponder(source);
            adapter.QueryResponder = sql => responder.Respond(sql, 100);
            AppServices.DatabaseAdapter = adapter;
        }

        private static void ApplyOpenUi(MigrationView mv, string openUi, bool fakeOracle)
        {
            if (string.IsNullOrEmpty(openUi))
            {
                return;
            }

            mv.Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(120).ConfigureAwait(true);
                switch (openUi)
                {
                    case "sql-editor":
                        mv.OpenSqlEditor();
                        break;
                    case "add-mapping":
                        mv.GoToStep(1);
                        mv.OpenAddMapping(null, false);
                        break;
                    case "auto-match":
                        mv.AutoMatchTables();
                        break;
                    case "template-export":
                        mv.ExportTemplate();
                        break;
                    case "rail-extra":
                        mv.OpenRailExtra();
                        break;
                }
            });
        }

        private static void SeedAll(FakePluginManager manager)
        {
            var settings = MigrationSettingsStore.CreateDefaultSettings();
            settings.Connections.Add(MakeProfile("cn-legacy-prod", "LEGACY_PROD", "red", "10.10.10.21", "LEGACY", "LEGACY_APP", "LEGACY_APP", true));
            settings.Connections.Add(MakeProfile("cn-legacy-dev", "LEGACY_DEV", "green", "10.10.30.11", "LEGACYDEV", "LEGACY_APP", "LEGACY_APP", false));
            settings.Connections.Add(MakeProfile("cn-next-prod", "NEXT_PROD", "red", "10.20.10.35", "NEXTDB", "NEXT_APP", "NEXT_APP", false));
            settings.Connections.Add(MakeProfile("cn-next-stg", "NEXT_STG", "yellow", "10.20.20.12", "NEXTSTG", "NEXT_APP", "NEXT_APP", false));
            settings.Connections.Add(new ConnectionProfile
            {
                Id = "it-local",
                Name = "IT_LOCAL",
                Host = "localhost",
                Port = 1521,
                Service = "xe",
                User = "MIG_IT_SRC",
                Color = "green",
                DefaultSchema = "MIG_IT_SRC"
            });
            manager.SetSetting(MigrationSettingsStore.SettingKey, MigrationSettingsStore.Serialize(settings));

            var job = JobSamples.SampleJob();
            JobDraftStore.Save(manager.DataDirectory, new JobDraft
            {
                Job = job,
                Dirty = true,
                FilePath = null,
                SavedAt = DateTime.Now
            });

            SeedMetadataCache(manager.DataDirectory, job);
            SeedMappingUiState(manager, job);
        }

        private static void SeedMappingUiState(FakePluginManager manager, MigrationJob job)
        {
            var draft = JobDraftStore.TryLoad(manager.DataDirectory);
            if (draft == null)
            {
                return;
            }

            draft.Job = job;
            JobDraftStore.Save(manager.DataDirectory, draft);
        }

        private static ConnectionProfile MakeProfile(string id, string name, string color, string host, string service, string user, string schema, bool writeBlocked)
        {
            var p = new ConnectionProfile
            {
                Id = id,
                Name = name,
                Kind = "oracle",
                Color = color,
                Host = host,
                Port = 1521,
                Service = service,
                User = user,
                DefaultSchema = schema,
                WriteBlocked = writeBlocked,
                SavePassword = false
            };
            return p;
        }

        private static void SeedMetadataCache(string dataDir, MigrationJob job)
        {
            var goldenPath = FindGoldenPath();
            if (string.IsNullOrEmpty(goldenPath))
            {
                return;
            }

            try
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(goldenPath)))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("source", out var sourceEl))
                    {
                        var meta = JsonSerializer.Deserialize<SchemaMetadata>(sourceEl.GetRawText(), MetadataJsonOptions.Options);
                        if (meta != null && job.Source != null)
                        {
                            meta.Cached = true;
                            meta.LoadedAt = DateTime.Now;
                            MetadataCache.Save(dataDir, job.Source.ProfileId, meta);
                        }
                    }

                    if (root.TryGetProperty("target", out var targetEl))
                    {
                        var meta = JsonSerializer.Deserialize<SchemaMetadata>(targetEl.GetRawText(), MetadataJsonOptions.Options);
                        if (meta != null && job.Target != null)
                        {
                            meta.Cached = true;
                            meta.LoadedAt = DateTime.Now;
                            MetadataCache.Save(dataDir, job.Target.ProfileId, meta);
                        }
                    }
                }
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }
        }

        private static string FindGoldenPath()
        {
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 10; i++)
            {
                if (string.IsNullOrEmpty(dir))
                {
                    break;
                }

                var candidate = Path.Combine(dir, "tests", "MigrationStudio.Tests", "Golden", "poc-golden.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                dir = Path.GetDirectoryName(dir);
            }

            // 별도 출력 폴더(-o)로 빌드했거나 저장소 밖에서 실행하면 위 탐색이 못 찾는다 — 환경 변수, 현재 폴더 위쪽, 이 소스의 위치 순으로 더 찾는다
            var env = Environment.GetEnvironmentVariable("MIGRATION_STUDIO_REPO");
            var roots = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(env))
            {
                roots.Add(env);
            }

            roots.Add(Environment.CurrentDirectory);
            roots.Add(@"D:\git\cshap\Folderss-oracle-data-migration");
            foreach (var r in roots)
            {
                var d = r;
                for (var i = 0; i < 6 && !string.IsNullOrEmpty(d); i++)
                {
                    var candidate = Path.Combine(d, "tests", "MigrationStudio.Tests", "Golden", "poc-golden.json");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    d = Path.GetDirectoryName(d);
                }
            }

            return null;
        }
    }
}
