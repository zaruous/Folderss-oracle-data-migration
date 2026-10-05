using System;
using System.Text.RegularExpressions;

namespace MigrationStudio.Core.Hosting
{
    public static class AgentPipeNames
    {
        private static readonly Regex RunIdPattern = new Regex("^[A-Za-z0-9-]+$", RegexOptions.CultureInvariant);

        public static string ForRun(string runId)
        {
            return "folderss-migration-" + runId;
        }

        public static bool IsValidRunId(string runId)
        {
            return !string.IsNullOrEmpty(runId) && RunIdPattern.IsMatch(runId);
        }

        public static bool MatchesRun(string pipeName, string runId)
        {
            return string.Equals(pipeName, ForRun(runId), StringComparison.Ordinal);
        }
    }
}
