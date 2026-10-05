using MigrationStudio.Core.Hosting;
using Xunit;

namespace MigrationStudio.Tests.Hosting
{
    public sealed class RunGuardTests
    {
        [Theory]
        [InlineData("waiting", true)]
        [InlineData("running", true)]
        [InlineData("paused", true)]
        [InlineData(null, true)]
        [InlineData("done", false)]
        [InlineData("stopped", false)]
        [InlineData("failed", false)]
        [InlineData("crashed", false)]
        [InlineData("FAILED", false)]
        public void Finished_runs_do_not_count_toward_concurrency_limit(string state, bool counts)
        {
            Assert.Equal(counts, RunGuard.CountsTowardLimit(new AgentRunInfo { RunId = "R", State = state }));
        }

        [Fact]
        public void Null_run_does_not_count()
        {
            Assert.False(RunGuard.CountsTowardLimit(null));
        }
    }
}
