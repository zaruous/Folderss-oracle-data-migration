using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace MigrationStudio.Core.Hosting
{
    public static class AgentProcessHelper
    {
        public static bool IsAlive(int pid, string expectedRunId, DateTime startedAt)
        {
            if (pid <= 0)
            {
                return false;
            }

            try
            {
                var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return false;
                }

                if (!string.Equals(process.ProcessName, "MigrationAgent", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (startedAt != DateTime.MinValue)
                {
                    var start = process.StartTime.ToUniversalTime();
                    var expected = startedAt.ToUniversalTime();
                    var delta = Math.Abs((start - expected).TotalSeconds);
                    if (delta > 5)
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static int CurrentProcessId()
        {
            return Process.GetCurrentProcess().Id;
        }
    }
}
