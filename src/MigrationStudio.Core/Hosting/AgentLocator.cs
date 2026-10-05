using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace MigrationStudio.Core.Hosting
{
    public sealed class AgentInstall
    {
        public string Version { get; set; }
        public string Directory { get; set; }
        public string ExePath { get; set; }
        public bool Copied { get; set; }
    }

    public static class AgentLocator
    {
        private const string InstallMutexName = @"Local\folderss-migration-agent-install";

        public static AgentInstall Prepare(string pluginDirectory, string dataDirectory)
        {
            if (string.IsNullOrEmpty(pluginDirectory) || string.IsNullOrEmpty(dataDirectory))
            {
                throw new AgentMissingException();
            }

            var sourceAgentDir = Path.Combine(pluginDirectory, "agent");
            var sourceExe = Path.Combine(sourceAgentDir, "MigrationAgent.exe");
            if (!File.Exists(sourceExe))
            {
                throw new AgentMissingException();
            }

            var version = ReadAgentVersion(sourceExe);
            var targetRoot = Path.Combine(dataDirectory, "agent");
            var targetDir = Path.Combine(targetRoot, SanitizeVersionFolder(version));
            var targetExe = Path.Combine(targetDir, "MigrationAgent.exe");

            using (var mutex = new Mutex(false, InstallMutexName))
            {
                mutex.WaitOne();
                try
                {
                    Directory.CreateDirectory(targetRoot);
                    if (Directory.Exists(targetDir) && File.Exists(targetExe))
                    {
                        var same = HashEquals(sourceAgentDir, targetDir);
                        if (same)
                        {
                            return new AgentInstall { Version = version, Directory = targetDir, ExePath = targetExe, Copied = false };
                        }
                    }

                    var temp = Path.Combine(targetRoot, ".tmp-" + Guid.NewGuid().ToString("N"));
                    CopyDirectory(sourceAgentDir, temp);
                    if (Directory.Exists(targetDir))
                    {
                        Directory.Delete(targetDir, true);
                    }

                    Directory.Move(temp, targetDir);
                    return new AgentInstall { Version = version, Directory = targetDir, ExePath = targetExe, Copied = true };
                }
                finally
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch
                    {
                    }
                }
            }
        }

        public static IReadOnlyList<string> InstalledVersions(string dataDirectory)
        {
            var root = Path.Combine(dataDirectory ?? "", "agent");
            if (!Directory.Exists(root))
            {
                return Array.Empty<string>();
            }

            return Directory.GetDirectories(root)
                .Select(Path.GetFileName)
                .Where(n => n != null && !n.StartsWith(".tmp-", StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }

        public static void CleanOld(string dataDirectory, string keepVersion, ISet<int> livePids)
        {
            var root = Path.Combine(dataDirectory ?? "", "agent");
            if (!Directory.Exists(root))
            {
                return;
            }

            var liveDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pids = livePids ?? (ISet<int>)new HashSet<int>();
            foreach (var pid in pids)
            {
                try
                {
                    var process = System.Diagnostics.Process.GetProcessById(pid);
                    var exe = process.MainModule != null ? process.MainModule.FileName : null;
                    if (!string.IsNullOrEmpty(exe))
                    {
                        liveDirs.Add(Path.GetDirectoryName(exe));
                    }
                }
                catch
                {
                }
            }

            foreach (var dir in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (name != null && name.StartsWith(".tmp-", StringComparison.Ordinal))
                {
                    try
                    {
                        Directory.Delete(dir, true);
                    }
                    catch
                    {
                    }

                    continue;
                }

                if (!string.IsNullOrEmpty(keepVersion) && string.Equals(name, SanitizeVersionFolder(keepVersion), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (liveDirs.Contains(dir))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(dir, true);
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// MigrationAgent.exe는 .NET apphost(네이티브 PE)라 <see cref="AssemblyName.GetAssemblyName(string)"/>이
        /// "PE image does not have metadata"로 실패한다(실제 Folderss에 설치해 돌릴 때 [Plan] 단계에서 난 오류).
        /// 버전은 파일 버전 정보(InformationalVersion) → 옆의 관리 어셈블리 MigrationAgent.dll 순으로 읽는다.
        /// </summary>
        private static string ReadAgentVersion(string exePath)
        {
            var info = ReadInformationalVersion(exePath);
            var version = !string.IsNullOrEmpty(info) ? info : ReadManagedVersion(Path.ChangeExtension(exePath, ".dll")) ?? "0.0.0";
            var hash = HashFile(exePath).Substring(0, 8);
            return version + "+" + hash;
        }

        private static string ReadManagedVersion(string dllPath)
        {
            try
            {
                if (!File.Exists(dllPath))
                {
                    return null;
                }

                var asm = AssemblyName.GetAssemblyName(dllPath);
                return asm.Version != null ? asm.Version.ToString() : null;
            }
            catch (BadImageFormatException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static string ReadInformationalVersion(string exePath)
        {
            try
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
                return !string.IsNullOrEmpty(info.ProductVersion) ? info.ProductVersion : info.FileVersion;
            }
            catch
            {
                return null;
            }
        }

        private static string SanitizeVersionFolder(string version)
        {
            if (string.IsNullOrEmpty(version))
            {
                return "unknown";
            }

            var sb = new StringBuilder();
            foreach (var ch in version)
            {
                if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '.' || ch == '+' || ch == '-' || ch == '_')
                {
                    sb.Append(ch);
                }
                else
                {
                    sb.Append('_');
                }
            }

            return sb.ToString();
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            }

            foreach (var dir in Directory.GetDirectories(source))
            {
                CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
            }
        }

        private static bool HashEquals(string sourceDir, string targetDir)
        {
            var sourceFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
            var targetFiles = Directory.GetFiles(targetDir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (sourceFiles.Count != targetFiles.Count)
            {
                return false;
            }

            for (var i = 0; i < sourceFiles.Count; i++)
            {
                var rel = sourceFiles[i].Substring(sourceDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var expected = Path.Combine(targetDir, rel);
                if (!File.Exists(expected))
                {
                    return false;
                }

                if (!string.Equals(HashFile(sourceFiles[i]), HashFile(expected), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                return Convert.ToHexString(sha.ComputeHash(stream));
            }
        }
    }
}
