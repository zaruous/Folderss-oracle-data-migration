using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Engine;
using Xunit;

namespace MigrationStudio.Tests.Engine
{
    /// <summary>삭제 대조의 키 비교: 해시 순서로 나란히 읽고, 같은 해시 묶음 안에서는 문자열로 비교한다.</summary>
    public sealed class KeyDiffTests
    {
        [Fact]
        public async Task Classifies_target_only_reappeared_already_marked_and_source_only()
        {
            var source = Keys("a", "b", "c");
            var target = Keys("b*", "c", "d", "e*");

            var diff = await KeyDiff.RunAsync(source, target, 100, CancellationToken.None);

            Assert.Equal(3, diff.SourceKeys);
            Assert.Equal(4, diff.TargetKeys);
            Assert.Equal(new[] { "d" }, diff.Mark.Select(k => k.Text));
            Assert.Equal(new[] { "b" }, diff.Unmark.Select(k => k.Text));
            Assert.Equal(1, diff.MarkCount);
            Assert.Equal(1, diff.UnmarkCount);
            Assert.Equal(1, diff.AlreadyMarked);
            Assert.Equal(1, diff.SourceOnly);
            Assert.False(diff.Truncated);
        }

        [Fact]
        public async Task Hash_collisions_are_resolved_by_text_even_across_batch_boundaries()
        {
            // 모든 키의 해시가 0이고 한 번에 1개씩만 읽힌다 — 묶음 비교와 배치 경계 처리를 같이 본다
            var source = Keys(h => 0, 1, "k1", "k2", "k3");
            var target = Keys(h => 0, 1, "k2", "k3", "k4");

            var diff = await KeyDiff.RunAsync(source, target, 100, CancellationToken.None);

            Assert.Equal(new[] { "k4" }, diff.Mark.Select(k => k.Text));
            Assert.Equal(1, diff.SourceOnly);
            Assert.Equal(0, diff.UnmarkCount);
        }

        [Fact]
        public async Task Out_of_order_stream_stops_before_anything_is_marked()
        {
            var source = new ListStream(new List<KeyEntry> { new KeyEntry { Hash = 5, Text = "x" }, new KeyEntry { Hash = 3, Text = "y" } }, 10);
            var target = new ListStream(new List<KeyEntry> { new KeyEntry { Hash = 3, Text = "y", RowId = "r" } }, 10);

            await Assert.ThrowsAsync<InvalidOperationException>(() => KeyDiff.RunAsync(source, target, 100, CancellationToken.None));
        }

        [Fact]
        public async Task Collection_limit_counts_everything_but_keeps_only_the_limit()
        {
            var diff = await KeyDiff.RunAsync(Keys(), Keys("x", "y", "z"), 2, CancellationToken.None);

            Assert.Equal(3, diff.MarkCount);
            Assert.Equal(2, diff.Mark.Count);
            Assert.True(diff.Truncated);
        }

        [Fact]
        public void Supported_key_types_exclude_time_zone_and_lobs()
        {
            Assert.True(KeyDiff.SupportsKeyType("NUMBER(18)"));
            Assert.True(KeyDiff.SupportsKeyType("VARCHAR2(30 CHAR)"));
            Assert.True(KeyDiff.SupportsKeyType("CHAR(5)"));
            Assert.True(KeyDiff.SupportsKeyType("DATE"));
            Assert.True(KeyDiff.SupportsKeyType("TIMESTAMP(6)"));
            Assert.False(KeyDiff.SupportsKeyType("TIMESTAMP(6) WITH TIME ZONE"));
            Assert.False(KeyDiff.SupportsKeyType("CLOB"));
            Assert.False(KeyDiff.SupportsKeyType("RAW(16)"));
        }

        /// <summary>"b*"처럼 별표가 붙으면 이미 표시된 대상 행.</summary>
        private static ListStream Keys(params string[] texts)
        {
            return Keys(t => (long)(uint)StringComparer.Ordinal.GetHashCode(t), 3, texts);
        }

        private static ListStream Keys(Func<string, long> hash, int batch, params string[] texts)
        {
            var entries = texts.Select(t =>
            {
                var marked = t.EndsWith("*", StringComparison.Ordinal);
                var text = marked ? t.Substring(0, t.Length - 1) : t;
                return new KeyEntry { Text = text, Hash = hash(text), RowId = "rid-" + text, Marked = marked };
            }).OrderBy(e => e.Hash).ToList();
            return new ListStream(entries, batch);
        }

        private sealed class ListStream : IKeyStream
        {
            private readonly List<KeyEntry> _entries;
            private readonly int _batch;
            private int _position;

            internal ListStream(List<KeyEntry> entries, int batch)
            {
                _entries = entries;
                _batch = batch;
            }

            public Task<List<KeyEntry>> ReadAsync(int maxRows, CancellationToken cancellationToken)
            {
                var take = Math.Min(Math.Min(maxRows, _batch), _entries.Count - _position);
                var result = take <= 0 ? new List<KeyEntry>() : _entries.GetRange(_position, take);
                _position += Math.Max(0, take);
                return Task.FromResult(result);
            }

            public void Dispose() { }
        }
    }
}
