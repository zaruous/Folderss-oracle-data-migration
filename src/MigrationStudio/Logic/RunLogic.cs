using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Hosting;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Validation;

namespace MigrationStudio.Logic
{
    /// <summary>실행 화면의 상태 문자열 모음.</summary>
    public static class RunStates
    {
        public const string Idle = "idle";
        public const string Starting = "starting";
        public const string Running = "running";
        public const string Pausing = "pausing";
        public const string Paused = "paused";
        public const string Done = "done";
        public const string Stopped = "stopped";
        public const string Failed = "failed";
        /// <summary>파이프가 끊겼지만 에이전트는 살아 있음.</summary>
        public const string Detached = "detached";
    }

    public sealed class ControlState
    {
        public bool CanStart { get; set; }
        public bool CanPause { get; set; }
        public bool CanResume { get; set; }
        public bool CanStop { get; set; }
        public bool CanChangeMode { get; set; }
    }

    public sealed class StageModel
    {
        public string Name { get; set; }
        public string Metric { get; set; }
        public string Sub { get; set; }
        public double Percent { get; set; }
        public bool Active { get; set; }
    }

    public sealed class TaskRowModel
    {
        public int Index { get; set; }
        public string Label { get; set; }
        public string Sub { get; set; }
        public string StatusText { get; set; }
        /// <summary>wait · run · paused · done · stopped · failed · skipped (배지 색)</summary>
        public string StatusKind { get; set; }
        public double Percent { get; set; }
        public string RowsText { get; set; }
        public string Inserted { get; set; }
        public string Updated { get; set; }
        public string Rejected { get; set; }
        public bool HasRejects { get; set; }
        public string Checkpoint { get; set; }
        public bool Running { get; set; }
    }

    public sealed class RunSummaryInput
    {
        public string State { get; set; }
        public double Pct { get; set; }
        public double Elapsed { get; set; }
        public bool Dry { get; set; }
        public int ResumablePct { get; set; } = -1;
    }

    /// <summary>시작 버튼을 눌렀을 때 거쳐야 하는 확인 하나.</summary>
    public enum StartGate
    {
        Proceed,
        NeedMetadata,
        NoSelection,
        NoCheckpointToResume,
        AskValidateFirst,
        Blocked,
        ConfirmStale,
        ConfirmDestructive,
        /// <summary>CDC(변경동기화) 시작: 창을 닫아도 계속 도는 실행이라 주기·종료 조건·대상을 보여 주고 확인을 받는다.</summary>
        ConfirmSync
    }

    public sealed class StartInputs
    {
        public bool HasMetadata { get; set; }
        public int SelectedCount { get; set; }
        public string Mode { get; set; }
        public bool HasValidation { get; set; }
        public bool ValidationRunning { get; set; }
        public bool ValidationStale { get; set; }
        public GateResult Gate { get; set; }
        public List<string> DestructiveLabels { get; set; } = new List<string>();
        public int SelectedWithCheckpoint { get; set; }
        /// <summary>사용자가 이미 넘긴 확인.</summary>
        public bool SkipValidateAsk { get; set; }
        public bool StaleAccepted { get; set; }
        public bool DestructiveAccepted { get; set; }
        /// <summary>전략이 CDC(변경동기화)이고 Dry Run이 아님 — 주기 반복 실행.</summary>
        public bool IsSync { get; set; }
        public bool SyncAccepted { get; set; }
    }

    /// <summary>실행 화면의 순수 논리(WPF 없음, 시험 대상).</summary>
    public static class RunLogic
    {
        // ---------- 실행할 작업 선택 ----------

        /// <summary>
        /// 실행 화면의 선택 집합을 지금 매핑 목록에 맞춘다. 처음 보는 매핑(사용 중)은 기본으로 고르고, 사라진 매핑은 뺀다 —
        /// 선택을 "한 번 초기화하고 끝"으로 두면 매핑이 없을 때 실행 화면이 먼저 만들어진 뒤 추가한 매핑이 영영 안 골라진다
        /// (실제 Folderss에서 "실행할 작업을 고르세요"만 나오고 시작이 안 되던 원인).
        /// </summary>
        public static void SyncSelection(ISet<string> selected, ISet<string> known, IEnumerable<Mapping> mappings)
        {
            if (selected == null || known == null)
            {
                return;
            }

            var all = (mappings ?? Enumerable.Empty<Mapping>()).Where(m => m != null && !string.IsNullOrEmpty(m.Id)).ToList();
            foreach (var m in all)
            {
                if (known.Add(m.Id) && m.Use)
                {
                    selected.Add(m.Id);
                }
            }

            selected.IntersectWith(all.Select(m => m.Id));
        }

        // ---------- 다시 붙기(창을 닫았다 다시 열 때) ----------

        /// <summary>살아 있는 에이전트 중 아직 끝나지 않은 것만 — 끝난 실행은 결과 재접속용으로 잠깐 더 살아 있을 뿐이다.</summary>
        public static List<AgentRunInfo> AliveForReattach(IEnumerable<AgentRunInfo> alive)
        {
            return (alive ?? Enumerable.Empty<AgentRunInfo>()).Where(RunGuard.CountsTowardLimit).ToList();
        }

        /// <summary>지금 열린 작업과 이름이 같은 실행이면 묻지 않고 바로 붙는다. 다른 작업의 실행은 안내만 하고 사용자가 고른다.</summary>
        public static AgentRunInfo PickReattach(IEnumerable<AgentRunInfo> alive, string jobName)
        {
            if (string.IsNullOrEmpty(jobName))
            {
                return null;
            }

            return AliveForReattach(alive).FirstOrDefault(r => string.Equals(r.JobName, jobName, StringComparison.Ordinal));
        }

        public static string ReattachNoticeText(IList<AgentRunInfo> alive)
        {
            var list = AliveForReattach(alive);
            if (list.Count == 0)
            {
                return "";
            }

            return ReattachNoticeText(alive, DateTime.Now);
        }

        public static string ReattachNoticeText(IList<AgentRunInfo> alive, DateTime now)
        {
            var list = AliveForReattach(alive);
            if (list.Count == 0)
            {
                return "";
            }

            var names = string.Join(", ", list.Select(r => (string.IsNullOrEmpty(r.JobName) ? "(이름 없음)" : r.JobName) + " · " + r.RunId + SyncTail(r, now)));
            return "진행 중인 실행 " + list.Count + "개가 다른 창에서 시작되어 아직 돌고 있습니다: " + names;
        }

        /// <summary>동기화 실행이면 "마지막 주기 n분 전". 오래됐으면(1시간 넘게) 멈춘 것일 수 있다고 덧붙인다.</summary>
        private static string SyncTail(AgentRunInfo run, DateTime now)
        {
            if (run == null || !run.LastCycleAt.HasValue)
            {
                return "";
            }

            var minutes = Math.Max(0, (now - run.LastCycleAt.Value).TotalMinutes);
            var ago = minutes < 1 ? "방금" : minutes < 60 ? Math.Floor(minutes) + "분 전" : Math.Floor(minutes / 60) + "시간 전";
            return " (동기화 주기 " + run.Cycle + " · 마지막 " + ago + (minutes >= 60 ? " — 멈췄을 수 있음, 붙어서 확인" : "") + ")";
        }

        public static bool IsActive(string state)
        {
            return state == RunStates.Starting || state == RunStates.Running || state == RunStates.Pausing || state == RunStates.Paused;
        }

        public static string StateText(string state, bool dry)
        {
            string text;
            switch (state)
            {
                case RunStates.Starting: text = "시작 중…"; break;
                case RunStates.Running: text = "실행 중"; break;
                case RunStates.Pausing: text = "일시 정지 요청…"; break;
                case RunStates.Paused: text = "일시 정지"; break;
                case RunStates.Done: text = "완료"; break;
                case RunStates.Stopped: text = "중지됨"; break;
                case RunStates.Failed: text = "실패"; break;
                case RunStates.Detached: text = "연결 끊김"; break;
                default: text = "대기"; break;
            }

            return (dry && state != RunStates.Idle ? "Dry Run · " : "") + text;
        }

        /// <summary>상태 Pill 종류: run · warn · ok · err · "" (보통).</summary>
        public static string PillKind(string state)
        {
            switch (state)
            {
                case RunStates.Starting:
                case RunStates.Running: return "run";
                case RunStates.Pausing:
                case RunStates.Paused:
                case RunStates.Stopped:
                case RunStates.Detached: return "warn";
                case RunStates.Done: return "ok";
                case RunStates.Failed: return "err";
                default: return "";
            }
        }

        public static ControlState Controls(string state, bool canStartNow)
        {
            return new ControlState
            {
                CanStart = canStartNow && !IsActive(state),
                CanPause = state == RunStates.Running,
                CanResume = state == RunStates.Paused,
                CanStop = state == RunStates.Running || state == RunStates.Pausing || state == RunStates.Paused,
                CanChangeMode = !IsActive(state)
            };
        }

        public static string StartButtonText(string mode)
        {
            return mode == "DRY" ? "Dry Run 시작" : mode == "RESUME" ? "재개 시작" : "이관 시작";
        }

        public static string ModeDescription(string mode)
        {
            return ModeDescription(mode, false);
        }

        /// <summary>전략 모드(FULL·INCREMENTAL·CDC)에 맞는 실행 모드 설명.</summary>
        public static string ModeDescription(string mode, string strategyMode)
        {
            if (string.Equals(strategyMode, ExecutionModes.Cdc, System.StringComparison.Ordinal))
            {
                if (mode == "DRY")
                {
                    return ModeDescription(mode, true);
                }

                return "CDC(변경동기화): 워터마크 다음 행을 읽어 반영한 뒤 주기만큼 기다렸다가 반복합니다. 중지하거나 최대 실행 시간이 될 때까지 계속 돌고, 창을 닫아도 에이전트가 계속합니다.";
            }

            return ModeDescription(mode, string.Equals(strategyMode, ExecutionModes.Incremental, System.StringComparison.Ordinal));
        }

        /// <summary>
        /// 삭제 대조 결과 줄(작업마다 한 줄). 따라가지 않는 매핑도 원본·대상 행 수 차이를 숨기지 않고 보인다.
        /// </summary>
        public static List<string> ReconcileLines(RunSnapshot snap, bool dry)
        {
            var lines = new List<string>();
            if (snap == null || snap.Tasks == null)
            {
                return lines;
            }

            foreach (var task in snap.Tasks)
            {
                var r = task.Reconcile;
                if (r == null)
                {
                    continue;
                }

                var head = task.Label + ": ";
                var at = " · " + r.At.ToString("HH:mm", CultureInfo.InvariantCulture) + " 대조";
                if (!string.IsNullOrEmpty(r.Error))
                {
                    lines.Add(head + "삭제 대조 실패 — " + r.Error.Split('\n')[0] + at);
                    continue;
                }

                if (!DeleteModes.IsMark(r.Mode))
                {
                    var diff = r.TargetRows - r.SourceRows;
                    lines.Add(head + "원본 " + N(r.SourceRows) + " · 대상 " + N(r.TargetRows) +
                        (diff > 0 ? " · 원본에 없는 대상 " + N(diff) + "행(삭제 따라가지 않음)" : diff < 0 ? " · 대상이 " + N(-diff) + "행 적음" : " · 일치") + at);
                    continue;
                }

                var samples = r.Samples != null && r.Samples.Count > 0 ? " (예: " + string.Join(" / ", r.Samples) + ")" : "";
                if (!string.IsNullOrEmpty(r.Blocked))
                {
                    lines.Add(head + "삭제 표시 멈춤 — " + r.Blocked + at);
                }
                else if (dry || !r.Approved)
                {
                    lines.Add(head + (dry ? "삭제 표시 예정 " : "삭제 표시 대기(승인 전) ") + N(r.MarkCandidates) + "행" +
                        (r.UnmarkCandidates > 0 ? " · 표시 해제 " + N(r.UnmarkCandidates) + "행" : "") + samples + at);
                }
                else
                {
                    lines.Add(head + "삭제 표시 " + N(r.Marked) + "행" + (r.Unmarked > 0 ? " · 표시 해제 " + N(r.Unmarked) + "행" : "") +
                        " · 이 실행 누적 " + N(r.TotalMarked) + " · 이미 표시 " + N(r.AlreadyMarked + r.Marked) + at);
                }
            }

            return lines;
        }

        /// <summary>끝난 실행에서 승인을 받을 수 있는 작업: 삭제 표시 매핑, 승인 전, 대조가 실패·멈추지 않음.</summary>
        public static List<TaskSnapshot> ApprovalCandidates(RunSnapshot snap)
        {
            var list = new List<TaskSnapshot>();
            if (snap == null || snap.Tasks == null)
            {
                return list;
            }

            foreach (var task in snap.Tasks)
            {
                var r = task.Reconcile;
                if (r != null && DeleteModes.IsMark(r.Mode) && !r.Approved && string.IsNullOrEmpty(r.Error) && string.IsNullOrEmpty(r.Blocked))
                {
                    list.Add(task);
                }
            }

            return list;
        }

        /// <summary>진행 카드의 동기화 한 줄. SYNC가 아니면 null.</summary>
        public static string SyncSummary(RunSnapshot snap, System.DateTime now)
        {
            if (snap == null || snap.Sync == null)
            {
                return null;
            }

            var s = snap.Sync;
            var text = "주기 " + s.Cycle;
            if (s.Phase == "waiting" && s.NextCycleAt.HasValue)
            {
                var wait = Math.Max(0, (s.NextCycleAt.Value - now).TotalSeconds);
                text += " 완료 · 다음 주기 " + s.NextCycleAt.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " (" + Duration(wait) + " 뒤)";
            }
            else if (s.Phase == "cycle")
            {
                text += " 실행 중";
            }

            if (s.LastCycleAt.HasValue)
            {
                text += " · 마지막 주기 " + s.LastCycleAt.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }

            text += " · 누적 삽입 " + N(s.Inserted) + " · 갱신 " + N(s.Updated) + (s.Rejected > 0 ? " · 거부 " + N(s.Rejected) : "");
            if (s.ConsecutiveFailures > 0)
            {
                text += " · 일시 오류 " + s.ConsecutiveFailures + "회 연속(재시도 중)";
            }

            return text;
        }

        /// <summary>실행 모드 설명. 전략이 증분 이관이면 실행·Dry Run은 워터마크(지난 실행의 마지막 키) 다음부터 읽는다는 점을 적는다.</summary>
        public static string ModeDescription(string mode, bool incremental)
        {
            switch (mode)
            {
                case "DRY":
                    return (incremental ? "워터마크 이후 행만 " : "") + "원본을 읽고 변환·매핑까지만 합니다. 대상에 쓰지 않고 예상 입력·갱신·거부 수를 보여 줍니다.";
                case "RESUME":
                    return "체크포인트가 있는 작업은 마지막 커밋 키 다음부터(WHERE 키 > :LAST_ID) 이어서 합니다.";
                default:
                    return incremental
                        ? "증분 이관: 매핑의 체크포인트 열 기준으로 지난 실행의 마지막 키(워터마크) 다음 행만 읽습니다. 워터마크가 없는 작업은 처음부터 읽습니다(첫 적재)."
                        : "처음부터 실행합니다. MERGE는 이미 있는 행을 갱신하므로 다시 실행해도 중복이 생기지 않습니다.";
            }
        }

        public static string PolicyText(string policy)
        {
            switch (policy)
            {
                case "CONTINUE": return "계속 + 오류 테이블";
                case "STOP": return "오류 시 중지";
                case "RETRY": return "3회 재시도";
                default: return policy ?? "";
            }
        }

        // ---------- 숫자 표기 ----------

        public static string N(long value)
        {
            return value.ToString("N0", CultureInfo.GetCultureInfo("en-US"));
        }

        public static string Rate(double perSecond)
        {
            return perSecond <= 0 ? "—" : Math.Round(perSecond).ToString("N0", CultureInfo.GetCultureInfo("en-US"));
        }

        /// <summary>1234 → 00:20:34</summary>
        public static string Duration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            {
                return "—";
            }

            var s = (long)Math.Max(0, Math.Round(seconds));
            return (s / 3600).ToString("00", CultureInfo.InvariantCulture) + ":" + ((s / 60) % 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (s % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>48,210,000 → 48.2M · 12,400 → 12K · 그 밖은 쉼표 숫자.</summary>
        public static string Short(long v)
        {
            if (v >= 10000000)
            {
                return (v / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + "M";
            }

            if (v >= 1000000)
            {
                return (v / 1e6).ToString("0.00", CultureInfo.InvariantCulture) + "M";
            }

            if (v >= 10000)
            {
                return Math.Round(v / 1e3).ToString("0", CultureInfo.InvariantCulture) + "K";
            }

            return N(v);
        }

        public static double Pct(long part, long total)
        {
            if (total <= 0)
            {
                return 0;
            }

            return Math.Min(100, Math.Max(0, part * 100.0 / total));
        }

        // ---------- 파이프라인 · 작업별 표 ----------

        public static List<StageModel> Pipeline(RunSnapshot snap, int fetchSize, int commitSize, int workers, string modeLabel)
        {
            var running = snap != null && snap.Tasks != null && snap.Tasks.Any(t => t.Status == "run");
            var cur = snap != null && snap.Tasks != null ? snap.Tasks.FirstOrDefault(t => t.Status == "run") ?? snap.Tasks.LastOrDefault(t => t.Status == "paused") : null;
            var dry = snap != null && snap.Mode == "DRY";
            var p = snap != null ? snap.Pipeline : null;
            var rate = running && p != null ? Rate(p.ReadRowsPerSecond) + " 행/초" : "대기";
            var fetches = cur != null && fetchSize > 0 ? (long)Math.Ceiling(cur.Read / (double)fetchSize) : 0;
            var commits = snap != null && snap.Tasks != null ? snap.Tasks.Sum(t => (long)t.Commits) : 0;
            var list = new List<StageModel>
            {
                new StageModel { Name = "원본 읽기", Metric = rate, Sub = cur != null ? "Fetch " + N(fetchSize) + " × " + N(fetches) + "회" : "Fetch " + N(fetchSize), Percent = running && p != null ? p.BufferPercent : 0, Active = running },
                new StageModel { Name = "Transform", Metric = running ? rate : "대기", Sub = "Oracle SELECT 안에서", Percent = running ? 70 : 0, Active = running },
                new StageModel { Name = "컬럼 매핑", Metric = running && cur != null ? "→ " + ShortLabel(cur.Label) : "대기", Sub = "별칭 → 대상 열", Percent = running ? 70 : 0, Active = running },
                new StageModel
                {
                    Name = "배치 쓰기",
                    Metric = running ? (dry ? "쓰기 없음(Dry)" : modeLabel ?? "") : "대기",
                    Sub = "배열 " + N(commitSize) + " × " + (running && p != null ? p.WritingWorkers : workers),
                    Percent = running && cur != null && commitSize > 0 ? Math.Min(100, cur.Pending * 100.0 / commitSize) : 0,
                    Active = running && !dry
                },
                new StageModel { Name = "커밋", Metric = snap != null ? N(commits) + "회" : "—", Sub = dry ? "롤백(Dry)" : "COMMIT", Percent = 0, Active = false },
                new StageModel
                {
                    Name = "체크포인트",
                    Metric = cur != null && !string.IsNullOrEmpty(cur.Checkpoint) ? cur.Checkpoint : "—",
                    Sub = dry ? "저장 안 함(Dry)" : "커밋마다 저장",
                    Percent = 0,
                    Active = false
                }
            };
            return list;
        }

        private static string ShortLabel(string label)
        {
            if (string.IsNullOrEmpty(label))
            {
                return "";
            }

            var i = label.IndexOf("→", StringComparison.Ordinal);
            return i >= 0 ? label.Substring(i + 1).Trim() : label;
        }

        public static TaskRowModel TaskRow(int index, TaskSnapshot t, long baseRows)
        {
            string text;
            string kind;
            switch (t.Status)
            {
                case "run": text = "실행 중"; kind = "run"; break;
                case "paused": text = "일시 정지"; kind = "paused"; break;
                case "done": text = "완료"; kind = "done"; break;
                case "stopped": text = "중지"; kind = "stopped"; break;
                case "failed": text = "실패"; kind = "failed"; break;
                case "skipped": text = "건너뜀"; kind = "skipped"; break;
                default: text = "대기"; kind = "wait"; break;
            }

            var total = baseRows + t.Total;
            var written = baseRows + t.Written;
            return new TaskRowModel
            {
                Index = index,
                Label = t.Label,
                StatusText = text,
                StatusKind = kind,
                Percent = Pct(written, total),
                RowsText = N(written) + " / " + Short(total),
                Inserted = N(t.Inserted),
                Updated = N(t.Updated),
                Rejected = N(t.Rejected),
                HasRejects = t.Rejected > 0,
                Checkpoint = string.IsNullOrEmpty(t.Checkpoint) ? "—" : t.Checkpoint,
                Running = t.Status == "run"
            };
        }

        // ---------- 로그 ----------

        /// <summary>로그 거르기 분류: info · warn · error.</summary>
        public static string LogClass(string tag)
        {
            if (tag == "WARN" || tag == "PAUSE")
            {
                return "warn";
            }

            if (tag == "ERROR" || tag == "STOP")
            {
                return "error";
            }

            return "info";
        }

        public static bool LogVisible(string tag, string filter)
        {
            return string.IsNullOrEmpty(filter) || filter == "all" || LogClass(tag) == filter;
        }

        // ---------- 체크포인트 카드 ----------

        public static string CheckpointWhere(string column, string value)
        {
            if (string.IsNullOrEmpty(column))
            {
                return "";
            }

            decimal number;
            var numeric = decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out number);
            return "WHERE " + column + " > " + (numeric ? value : "'" + (value ?? "").Replace("'", "''") + "'") + "\nORDER BY " + column;
        }

        public static string CheckpointStatusText(string status)
        {
            return status == "done" ? "완료" : status == "running" ? "진행 중" : "중단됨";
        }

        // ---------- 시작 흐름 ----------

        /// <summary>시작 버튼을 눌렀을 때 거쳐야 할 첫 번째 확인(없으면 Proceed). 사용자가 확인을 넘길 때마다 플래그를 켜고 다시 부른다.</summary>
        public static StartGate NextStartGate(StartInputs i)
        {
            if (!i.HasMetadata)
            {
                return StartGate.NeedMetadata;
            }

            if (i.SelectedCount == 0)
            {
                return StartGate.NoSelection;
            }

            if (i.Mode == "RESUME" && i.SelectedWithCheckpoint == 0)
            {
                return StartGate.NoCheckpointToResume;
            }

            if (i.Mode == "DRY")
            {
                return StartGate.Proceed;
            }

            if (!i.HasValidation && !i.SkipValidateAsk)
            {
                return StartGate.AskValidateFirst;
            }

            if (i.HasValidation && i.Gate != null && i.Gate.Blocked)
            {
                return StartGate.Blocked;
            }

            if (i.HasValidation && i.ValidationStale && !i.StaleAccepted)
            {
                return StartGate.ConfirmStale;
            }

            if (i.DestructiveLabels != null && i.DestructiveLabels.Count > 0 && !i.DestructiveAccepted)
            {
                return StartGate.ConfirmDestructive;
            }

            if (i.IsSync && !i.SyncAccepted)
            {
                return StartGate.ConfirmSync;
            }

            return StartGate.Proceed;
        }

        public static bool IsDestructive(string writeMode)
        {
            return WriteModes.Of(writeMode).Destructive;
        }

        // ---------- 단계 막대 · 상태줄 ----------

        public static StepInfo StepSummary(RunSummaryInput run)
        {
            if (run == null || run.State == RunStates.Idle)
            {
                if (run != null && run.ResumablePct >= 0)
                {
                    return new StepInfo { State = "warn", Summary = "재개 가능 · " + run.ResumablePct + "%" };
                }

                return new StepInfo { State = "", Summary = "대기" };
            }

            var pct = Math.Floor(run.Pct) + "%";
            switch (run.State)
            {
                case RunStates.Starting:
                case RunStates.Running:
                case RunStates.Pausing:
                    return new StepInfo { State = "busy", Summary = (run.Dry ? "Dry Run " : "실행 중 ") + pct };
                case RunStates.Paused:
                    return new StepInfo { State = "warn", Summary = "일시 정지 " + pct };
                case RunStates.Done:
                    return new StepInfo { State = "done", Summary = (run.Dry ? "Dry Run 완료" : "완료") + " · " + Duration(run.Elapsed) };
                case RunStates.Stopped:
                    return new StepInfo { State = "warn", Summary = "중지 · 재개 가능 " + pct };
                case RunStates.Detached:
                    return new StepInfo { State = "warn", Summary = "연결 끊김 · " + pct };
                default:
                    return new StepInfo { State = "error", Summary = "실패 · " + pct };
            }
        }

        /// <summary>상태줄 실행 문구: "실행 중 · 850,000 / 1,240,325 행 (68%)"</summary>
        public static string StatusBarText(string state, bool dry, long done, long total, double pct)
        {
            if (state == RunStates.Idle || state == null)
            {
                return "대기";
            }

            return StateText(state, dry) + " · " + N(done) + " / " + N(total) + " 행 (" + Math.Floor(pct) + "%)";
        }
    }
}
