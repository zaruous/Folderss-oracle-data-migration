using System;
using System.Globalization;
using System.IO;

namespace MigrationStudio.Services
{
    /// <summary>
    /// 화면 쪽 진단 기록(<c>DataDirectory\logs\ui-trace.log</c>). 에이전트 로그는 실행마다 있지만, 창을 열 때 다시 붙기가 왜 안 됐는지 같은
    /// "실행 밖" 일은 남는 데가 없어 실제 Folderss에서 재현이 어려웠다. 실패해도 조용히 넘어간다.
    /// </summary>
    internal static class UiTrace
    {
        private static readonly object Gate = new object();

        public static void Write(string area, string message)
        {
            try
            {
                var dir = AppServices.DataDirectory;
                if (string.IsNullOrEmpty(dir))
                {
                    return;
                }

                var logs = Path.Combine(dir, "logs");
                Directory.CreateDirectory(logs);
                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " [" + area + "] " + message + Environment.NewLine;
                lock (Gate)
                {
                    File.AppendAllText(Path.Combine(logs, "ui-trace.log"), line);
                }
            }
            catch
            {
            }
        }
    }
}
