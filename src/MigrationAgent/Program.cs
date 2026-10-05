using System;
using System.Globalization;
using System.Reflection;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Hosting;

namespace MigrationAgent
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args != null && args.Length == 1 && string.Equals(args[0], "--version", StringComparison.Ordinal))
                {
                    var version = Assembly.GetExecutingAssembly()
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                        ?? "0.0.0";
                    Console.WriteLine("MigrationAgent " + version + " (protocol " + AgentProtocol.Version + ")");
                    return 0;
                }

                string runId = null;
                string pipe = null;
                string data = null;
                var parent = 0;
                var idleExit = 600;
                for (var i = 0; args != null && i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--run", StringComparison.Ordinal) && i + 1 < args.Length)
                    {
                        runId = args[++i];
                    }
                    else if (string.Equals(args[i], "--pipe", StringComparison.Ordinal) && i + 1 < args.Length)
                    {
                        pipe = args[++i];
                    }
                    else if (string.Equals(args[i], "--data", StringComparison.Ordinal) && i + 1 < args.Length)
                    {
                        data = args[++i];
                    }
                    else if (string.Equals(args[i], "--parent", StringComparison.Ordinal) && i + 1 < args.Length)
                    {
                        int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out parent);
                    }
                    else if (string.Equals(args[i], "--idle-exit", StringComparison.Ordinal) && i + 1 < args.Length)
                    {
                        int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out idleExit);
                    }
                    else
                    {
                        Usage();
                        return 2;
                    }
                }

                if (string.IsNullOrEmpty(runId) || string.IsNullOrEmpty(pipe) || string.IsNullOrEmpty(data))
                {
                    Usage();
                    return 2;
                }

                if (!AgentPipeNames.IsValidRunId(runId) || !AgentPipeNames.MatchesRun(pipe, runId))
                {
                    Console.Error.WriteLine("파이프 이름이 RUN_ID와 맞지 않습니다.");
                    return 2;
                }

                var startWait = 60;
                var startWaitEnv = Environment.GetEnvironmentVariable("MIGRATION_AGENT_START_WAIT_SECONDS");
                if (!string.IsNullOrEmpty(startWaitEnv))
                {
                    int.TryParse(startWaitEnv, NumberStyles.Integer, CultureInfo.InvariantCulture, out startWait);
                }

                var host = new AgentHost(runId, pipe, data, idleExit, startWait);
                return host.RunAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static void Usage()
        {
            Console.Error.WriteLine("사용법: MigrationAgent.exe --run <RUN_ID> --pipe <파이프이름> --data <DataDirectory> [--parent <PID>] [--idle-exit <초>]");
            Console.Error.WriteLine("       MigrationAgent.exe --version");
        }
    }
}
