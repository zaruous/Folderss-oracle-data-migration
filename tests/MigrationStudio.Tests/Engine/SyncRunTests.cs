using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using MigrationStudio.Tests.Engine.Fakes;
using Xunit;
using World = MigrationStudio.Tests.Engine.IncrementalRunTests.World;

namespace MigrationStudio.Tests.Engine
{
    /// <summary>
    /// SYNC(CDC 변경동기화): [한 번 돌기 → 워터마크 → 대기]를 반복하는 엔진 루프. 주기 사이 대기는 가짜 시계를 움직이는 함수로 바꿔 끼운다.
    /// </summary>
    public sealed class SyncRunTests
    {
        [Fact]
        public async Task Sync_repeats_cycles_from_watermark_until_stopped()
        {
            var world = new World(WriteModes.Merge, 10);
            MigrationEngine engine = null;
            var mutated = false;
            var run = await world.RunAsync(ExecutionModes.Cdc, RunModes.Sync, null, e =>
            {
                engine = e;
                e.CycleDelay = (span, ct) =>
                {
                    world.Clock.Advance(span);
                    var sync = e.Snapshot().Sync;
                    if (sync.Cycle == 1 && !mutated)
                    {
                        mutated = true;
                        world.Source.Rows.Add(IncrementalRunTests.Row(11));
                        world.Source.Rows.Add(IncrementalRunTests.Row(12));
                        world.Touch(3, 13);
                    }
                    if (sync.Cycle >= 2)
                    {
                        e.Stop();
                    }
                    return Task.CompletedTask;
                };
            });

            Assert.Equal("stopped", run.Engine.State);
            Assert.Equal(12, world.Target.Rows.Count);
            Assert.Equal("NAME-3-v13", world.TargetName(3));
            var final = run.Listener.Final;
            Assert.NotNull(final.Sync);
            Assert.Equal(2, final.Sync.Cycle);
            Assert.Equal(13, final.Sync.Written);
            Assert.Equal(12, final.Sync.Inserted);
            Assert.Equal(1, final.Sync.Updated);
            Assert.Equal(60, final.Sync.IntervalSeconds);
            Assert.Contains(run.Listener.Logs, l => l.Tag == "CYCLE" && l.Text.StartsWith("주기 1 완료 · 새 10행", StringComparison.Ordinal));
            Assert.Contains(run.Listener.Logs, l => l.Tag == "CYCLE" && l.Text.StartsWith("주기 2 완료 · 새 3행 (삽입 2 · 갱신 1)", StringComparison.Ordinal));
            Assert.Contains(run.Listener.Logs, l => l.Tag == "START" && l.Text.Contains("CDC(변경동기화)", StringComparison.Ordinal));
            Assert.Equal(IncrementalRunTests.Stamp(13), (await world.Store.GetAsync("JOB", "M1", CancellationToken.None)).Value);
            // 대기 중 스냅숏은 다음 주기 시각을 알려 준다
            Assert.Contains(run.Listener.Snapshots, s => s.Sync != null && s.Sync.Phase == "waiting" && s.Sync.NextCycleAt.HasValue && s.Sync.LastCycleAt.HasValue);
        }

        [Fact]
        public async Task Sync_second_run_continues_from_saved_watermark()
        {
            var world = new World(WriteModes.Merge, 10);
            await world.RunAsync(ExecutionModes.Cdc, RunModes.Sync, null, e => e.CycleDelay = (span, ct) => { e.Stop(); return Task.CompletedTask; });
            world.Source.Rows.Add(IncrementalRunTests.Row(11));

            var second = await world.RunAsync(ExecutionModes.Cdc, RunModes.Sync, null, e => e.CycleDelay = (span, ct) => { e.Stop(); return Task.CompletedTask; });

            Assert.Equal(IncrementalRunTests.Stamp(10), second.Plan[0].ResumeFrom);
            Assert.Equal(1, second.Listener.Final.Sync.Written);
            Assert.Equal(11, world.Target.Rows.Count);
        }

        [Fact]
        public async Task Sync_transient_failure_backs_off_and_keeps_watermark()
        {
            var world = new World(WriteModes.Merge, 10);
            var run = await world.RunAsync(ExecutionModes.Cdc, RunModes.Sync, null, e =>
            {
                world.TargetFactory.FailWrites = 2;
                e.CycleDelay = (span, ct) =>
                {
                    world.Clock.Advance(span);
                    if (e.Snapshot().Sync.ConsecutiveFailures == 0 && e.Snapshot().Sync.Cycle >= 3)
                    {
                        e.Stop();
                    }
                    return Task.CompletedTask;
                };
            });

            Assert.Equal("stopped", run.Engine.State);
            Assert.Equal(10, world.Target.Rows.Count);
            Assert.Equal(3, run.Listener.Final.Sync.Cycle);
            Assert.Contains(run.Listener.Logs, l => l.Tag == "WARN" && l.Text.Contains("다시 시도 1/5", StringComparison.Ordinal) && l.Text.Contains("00:01:00 뒤", StringComparison.Ordinal));
            Assert.Contains(run.Listener.Logs, l => l.Tag == "WARN" && l.Text.Contains("다시 시도 2/5", StringComparison.Ordinal) && l.Text.Contains("00:02:00 뒤", StringComparison.Ordinal));
            Assert.Contains(run.Listener.Logs, l => l.Tag == "CYCLE" && l.Text.StartsWith("주기 3 완료 · 새 10행", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Sync_gives_up_after_five_consecutive_transient_failures()
        {
            var world = new World(WriteModes.Merge, 10);
            var run = await world.RunAsync(ExecutionModes.Cdc, RunModes.Sync, null, e =>
            {
                world.TargetFactory.FailWrites = 100;
                e.CycleDelay = (span, ct) => { world.Clock.Advance(span); return Task.CompletedTask; };
            });

            Assert.Equal("failed", run.Engine.State);
            Assert.Equal(6, run.Listener.Final.Sync.Cycle);
            Assert.Contains(run.Listener.Logs, l => l.Tag == "ERROR" && l.Text.Contains("5번 연속", StringComparison.Ordinal));
            Assert.Empty(world.Target.Rows);
        }

        [Fact]
        public async Task Sync_data_error_stops_without_retry()
        {
            // 워터마크가 생긴 뒤의 TRUNCATE + INSERT는 설정 오류 — 재시도 없이 멈춘다
            var world = new World(WriteModes.TruncateInsert, 10);
            var run = await world.RunAsync(ExecutionModes.Cdc, RunModes.Sync, null, e =>
                e.CycleDelay = (span, ct) => { world.Clock.Advance(span); return Task.CompletedTask; });

            Assert.Equal("failed", run.Engine.State);
            Assert.Equal(2, run.Listener.Final.Sync.Cycle);
            Assert.Equal(10, world.Target.Rows.Count);
            Assert.Contains(run.Listener.Logs, l => l.Tag == "ERROR" && l.Text.Contains("데이터·설정 오류", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Sync_ends_as_done_when_max_run_hours_reached()
        {
            var world = new World(WriteModes.Merge, 10);
            var run = await world.RunAsync(ExecutionModes.Cdc, RunModes.Sync,
                s => { s.PollIntervalSeconds = 3600; s.MaxRunHours = 1; },
                e => e.CycleDelay = (span, ct) => { world.Clock.Advance(span); return Task.CompletedTask; });

            Assert.Equal("done", run.Engine.State);
            Assert.Equal(2, run.Listener.Final.Sync.Cycle);
            Assert.Contains(run.Listener.Logs, l => l.Tag == "DONE" && l.Text.Contains("최대 실행 시간 1시간 도달", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Sync_pauses_between_cycles_and_resumes()
        {
            var world = new World(WriteModes.Merge, 10);
            var pausedSeen = false;
            var run = await world.RunAsync(ExecutionModes.Cdc, RunModes.Sync, null, e =>
            {
                var calls = 0;
                e.CycleDelay = (span, ct) =>
                {
                    world.Clock.Advance(span);
                    calls++;
                    if (calls == 1)
                    {
                        e.Pause();
                        // 일시 정지는 대기 조각이 끝난 직후 반영된다 — 다른 스레드에서 잠깐 뒤 이어서 실행
                        Task.Run(async () =>
                        {
                            while (e.State != "paused") await Task.Delay(10);
                            pausedSeen = true;
                            e.Resume();
                        });
                    }
                    if (e.Snapshot().Sync.Cycle >= 2)
                    {
                        e.Stop();
                    }
                    return Task.CompletedTask;
                };
            });

            Assert.True(pausedSeen);
            Assert.Equal("stopped", run.Engine.State);
            Assert.Equal(2, run.Listener.Final.Sync.Cycle);
            Assert.Contains(run.Listener.Logs, l => l.Tag == "PAUSE" && l.Text.Contains("주기 사이", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Planner_sync_uses_watermark_and_single_worker()
        {
            var world = new World(WriteModes.Merge, 10);
            await world.RunAsync(ExecutionModes.Cdc, RunModes.Sync, null, e => e.CycleDelay = (span, ct) => { e.Stop(); return Task.CompletedTask; });
            world.Source.Rows.Add(IncrementalRunTests.Row(11));

            var run = await world.RunAsync(ExecutionModes.Cdc, RunModes.Sync, s => s.Workers = 4, e => e.CycleDelay = (span, ct) => { e.Stop(); return Task.CompletedTask; });

            Assert.Single(run.Plan[0].Ranges);
            Assert.Equal(IncrementalRunTests.Stamp(10), run.Plan[0].ResumeFrom);
            Assert.Equal(1, run.Plan[0].ScopeTotal);
        }
    }
}
