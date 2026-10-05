using System;
using System.IO;
using MigrationStudio.Core.Hosting;
using Xunit;

namespace MigrationStudio.Tests.Hosting
{
    /// <summary>
    /// 실제 Folderss에 설치했을 때 [Plan] 단계가 "PE image does not have metadata"로 실패하던 회귀 —
    /// 플러그인 폴더의 agent\MigrationAgent.exe(apphost)로 <see cref="AgentLocator.Prepare"/>가 버전을 읽고 복사해야 한다.
    /// </summary>
    public sealed class AgentLocatorTests
    {
        [Fact]
        public void Prepare_reads_version_from_apphost_exe_and_copies_agent()
        {
            var built = LocateBuiltAgentDir();
            if (built == null)
            {
                return; // 에이전트를 아직 빌드하지 않은 환경(단독 실행)에서는 건너뛴다
            }

            var root = Path.Combine(Path.GetTempPath(), "mig-locator-" + Guid.NewGuid().ToString("N"));
            var pluginDir = Path.Combine(root, "plugin");
            var dataDir = Path.Combine(root, "data");
            try
            {
                var agentDir = Path.Combine(pluginDir, "agent");
                Directory.CreateDirectory(agentDir);
                foreach (var name in new[] { "MigrationAgent.exe", "MigrationAgent.dll", "MigrationAgent.runtimeconfig.json", "MigrationAgent.deps.json" })
                {
                    var src = Path.Combine(built, name);
                    if (File.Exists(src))
                    {
                        File.Copy(src, Path.Combine(agentDir, name));
                    }
                }

                var install = AgentLocator.Prepare(pluginDir, dataDir);
                Assert.True(File.Exists(install.ExePath), install.ExePath);
                Assert.StartsWith("1.", install.Version);
                Assert.Contains("+", install.Version);

                // 같은 내용이면 두 번째는 복사하지 않는다
                var again = AgentLocator.Prepare(pluginDir, dataDir);
                Assert.False(again.Copied);
                Assert.Equal(install.Version, again.Version);
            }
            finally
            {
                try
                {
                    Directory.Delete(root, true);
                }
                catch
                {
                }
            }
        }

        private static string LocateBuiltAgentDir()
        {
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                foreach (var cfg in new[] { "Release", "Debug" })
                {
                    var built = Path.Combine(dir, "src", "MigrationAgent", "bin", cfg, "net8.0", "win-x64");
                    if (File.Exists(Path.Combine(built, "MigrationAgent.exe")))
                    {
                        return built;
                    }
                }

                dir = Path.GetDirectoryName(dir);
            }

            return null;
        }
    }
}
