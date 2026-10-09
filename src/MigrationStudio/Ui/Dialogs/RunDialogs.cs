using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using MigrationStudio.Core.Validation;
using MigrationStudio.Services;

namespace MigrationStudio.Ui.Modals
{
    /// <summary>실행 시작 흐름의 확인 팝업들(UI-MIG-006 2장·3장 "시작" 행). DevHost 캡처 모드에서는 블로킹하지 않고 창만 띄운다.</summary>
    internal static class RunDialogs
    {
        public enum ValidateFirstChoice
        {
            Cancel,
            Validate,
            RunAnyway
        }

        /// <summary>검증을 아직 안 했을 때: 검증 먼저 / 그냥 실행 / 취소.</summary>
        public static ValidateFirstChoice AskValidateFirst(Window owner)
        {
            var window = DialogKit.Create(owner, "검증을 아직 하지 않았습니다", 440);
            var body = DialogKit.Body(window);
            body.Children.Add(DialogKit.Text("실행 전 검증을 아직 하지 않았습니다. 지금 검증할까요?", 0));
            body.Children.Add(DialogKit.Hint("검증 없이 실행하면 길이·NOT NULL·키 문제를 실행 도중에야 알게 될 수 있습니다.", 8));
            var choice = ValidateFirstChoice.Cancel;
            var validate = DialogKit.PrimaryButton("검증 먼저");
            validate.Click += (s, e) => { choice = ValidateFirstChoice.Validate; window.DialogResult = true; };
            var anyway = DialogKit.PlainButton("그냥 실행");
            anyway.Click += (s, e) => { choice = ValidateFirstChoice.RunAnyway; window.DialogResult = true; };
            body.Children.Add(DialogKit.Buttons(DialogKit.CancelButton("취소", false), anyway, validate));
            return Show(window) ? choice : ValidateFirstChoice.Cancel;
        }

        /// <summary>고른 작업에 검증 ERROR가 있을 때(팝업 ②): 최대 5개를 보이고 [검증 결과 보기]를 고르면 true.</summary>
        public static bool ShowBlocked(Window owner, GateResult gate)
        {
            var window = DialogKit.Create(owner, "실행할 수 없음", 520);
            var body = DialogKit.Body(window);
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(DialogKit.Icon(true));
            var title = DialogKit.Text("고른 작업에 검증 ERROR가 " + gate.Errors + "건 있습니다. 고친 뒤 다시 검증하세요. Dry Run은 할 수 있습니다.", 0);
            title.Margin = new Thickness(10, 2, 0, 0);
            title.MaxWidth = 440;
            head.Children.Add(title);
            body.Children.Add(head);
            var list = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (var item in gate.Blocking.Take(5))
            {
                var line = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                var name = Theme.Text("• " + item.Check + (string.IsNullOrEmpty(item.Target) ? "" : " · " + item.Target));
                name.FontWeight = FontWeights.SemiBold;
                line.Children.Add(name);
                var first = (item.Detail ?? "").Split('\n')[0];
                var detail = Theme.Secondary(first);
                detail.TextWrapping = TextWrapping.Wrap;
                detail.Margin = new Thickness(12, 0, 0, 0);
                line.Children.Add(detail);
                list.Children.Add(line);
            }

            if (gate.Blocking.Count > 5)
            {
                list.Children.Add(Theme.Secondary("… 외 " + (gate.Blocking.Count - 5) + "건"));
            }

            body.Children.Add(list);
            var show = false;
            var results = DialogKit.PrimaryButton("검증 결과 보기");
            results.Click += (s, e) => { show = true; window.DialogResult = true; };
            body.Children.Add(DialogKit.Buttons(DialogKit.CancelButton("닫기", true), results));
            Show(window);
            return show;
        }

        /// <summary>검증한 뒤 작업이 바뀌었을 때: 그대로 실행하면 true.</summary>
        public static bool ConfirmStale(Window owner)
        {
            var window = DialogKit.Create(owner, "검증 뒤 작업이 바뀜", 440);
            var body = DialogKit.Body(window);
            body.Children.Add(DialogKit.Text("검증한 뒤 작업이 바뀌었습니다. 다시 검증하지 않고 실행할까요?", 0));
            var ok = false;
            var run = DialogKit.PrimaryButton("그대로 실행");
            run.Click += (s, e) => { ok = true; window.DialogResult = true; };
            body.Children.Add(DialogKit.Buttons(DialogKit.CancelButton("취소", true), run));
            Show(window);
            return ok;
        }

        /// <summary>되돌릴 수 없는 이관 방식(팝업 ①). 체크해야 [실행]이 켜진다.</summary>
        public static bool ConfirmDestructive(Window owner, ConnectionProfile target, string schema, IList<string> lines)
        {
            var window = DialogKit.Create(owner, "되돌릴 수 없는 이관 방식", 480);
            var body = DialogKit.Body(window);
            var production = target != null && target.Color == "red";
            body.Children.Add(DialogKit.Target(target, "대상" + (production ? " 운영 DB" : "") + "에서 아래 테이블의 기존 행을 지웁니다."));
            var list = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (var line in lines)
            {
                var t = Theme.Text("• " + line);
                t.FontFamily = Theme.Mono;
                t.FontSize = 12;
                t.Margin = new Thickness(0, 0, 0, 4);
                list.Children.Add(t);
            }

            body.Children.Add(list);
            if (production)
            {
                var warn = new TextBlock { Text = "운영 DB입니다. 지운 데이터는 되돌릴 수 없습니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = Theme.Warning };
                body.Children.Add(warn);
            }

            var check = DialogKit.Check("지워도 되는 것을 확인했습니다");
            body.Children.Add(check);
            var ok = false;
            var run = DialogKit.PrimaryButton("실행");
            run.Foreground = Theme.Danger;
            run.IsEnabled = false;
            check.Checked += (s, e) => run.IsEnabled = true;
            check.Unchecked += (s, e) => run.IsEnabled = false;
            run.Click += (s, e) => { ok = true; window.DialogResult = true; };
            body.Children.Add(DialogKit.Buttons(DialogKit.CancelButton("취소", true), run));
            Show(window);
            return ok;
        }

        /// <summary>CDC(변경동기화) 시작 확인. 창을 닫아도 도는 실행이라 "언제까지 · 얼마나 자주 · 어디에 쓰는지"를 한 번 더 보여 준다.</summary>
        public static bool ConfirmSync(Window owner, ConnectionProfile target, MigrationStrategy strategy, AgentSettings agent)
        {
            strategy = strategy ?? new MigrationStrategy();
            agent = agent ?? new AgentSettings();
            var window = DialogKit.Create(owner, "CDC(변경동기화) 시작", 500);
            var body = DialogKit.Body(window);
            var production = target != null && target.Color == "red";
            body.Children.Add(DialogKit.Target(target, "대상" + (production ? " 운영 DB" : "") + "에 주기마다 변경을 반영합니다."));
            var lines = new List<string>
            {
                "주기: " + strategy.PollIntervalSeconds + "초마다 워터마크 다음 행을 읽어 INSERT+UPDATE" +
                    (strategy.LagSeconds > 0 ? " · 지연 창 " + strategy.LagSeconds + "초(날짜 체크포인트는 그만큼 늦게 반영)" : ""),
                "종료: " + (strategy.MaxRunHours > 0 ? "최대 " + strategy.MaxRunHours + "시간 뒤 자동 종료 (또는 중지 버튼)" : "무기한 — 중지 버튼을 누를 때까지"),
                "창을 닫으면: " + (string.Equals(agent.OnHostExit, "STOP", System.StringComparison.OrdinalIgnoreCase)
                    ? "Folderss를 닫을 때 함께 중지 (설정 > 에이전트)"
                    : "Folderss를 닫아도 에이전트가 계속 동기화 (설정 > 에이전트에서 바꿀 수 있음)"),
                "절전·재부팅·로그아웃이면 멈추고 스스로 다시 시작하지 않음 — 다시 열면 워터마크 다음부터 이어 감",
                "동기화가 도는 동안 같은 작업의 다른 실행은 할 수 없음"
            };
            var list = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (var line in lines)
            {
                var t = Theme.Text("• " + line);
                t.TextWrapping = TextWrapping.Wrap;
                t.FontSize = 12;
                t.Margin = new Thickness(0, 0, 0, 4);
                list.Children.Add(t);
            }

            body.Children.Add(list);
            if (strategy.MaxRunHours <= 0 || production)
            {
                var warn = new TextBlock
                {
                    Text = (production ? "운영 DB에 상시로 씁니다. " : "") + (strategy.MaxRunHours <= 0 ? "종료 시간이 없어 잊으면 계속 돕니다." : ""),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = Theme.Warning
                };
                body.Children.Add(warn);
            }

            var check = DialogKit.Check("위 내용을 확인했습니다");
            body.Children.Add(check);
            var ok = false;
            var run = DialogKit.PrimaryButton("동기화 시작");
            run.IsEnabled = false;
            check.Checked += (s, e) => run.IsEnabled = true;
            check.Unchecked += (s, e) => run.IsEnabled = false;
            run.Click += (s, e) => { ok = true; window.DialogResult = true; };
            body.Children.Add(DialogKit.Buttons(DialogKit.CancelButton("취소", true), run));
            Show(window);
            return ok;
        }

        private static bool Show(Window window)
        {
            if (AppServices.DevHostCaptureMode)
            {
                AppServices.DevHostShotWindow = window;
                window.Show();
                return false;
            }

            return window.ShowDialog() == true;
        }
    }
}
