using System;
using System.Globalization;
using System.IO;
using System.Text;
using MigrationStudio.Core.Engine;

namespace MigrationStudio.Core.Hosting
{
    /// <summary>logs\&lt;RUN_ID&gt;.log 한 줄 형식(UTF-8, BOM 없음).</summary>
    public static class AgentLogFormat
    {
        public static string FormatLine(LogEntry entry)
        {
            var at = entry.At.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var tag = string.IsNullOrEmpty(entry.Tag) ? "INFO" : entry.Tag;
            var text = entry.Text ?? "";
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            if (text.IndexOf('\n') < 0)
            {
                return at + " [" + tag + "] " + text;
            }

            var parts = text.Split('\n');
            var sb = new StringBuilder();
            sb.Append(at).Append(" [").Append(tag).Append("] ").Append(parts[0]);
            for (var i = 1; i < parts.Length; i++)
            {
                sb.AppendLine();
                sb.Append("    ").Append(parts[i]);
            }

            return sb.ToString();
        }

        public static LogEntry ParseLine(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return null;
            }

            if (line.Length < 25 || line[4] != '-' || line[10] != ' ')
            {
                return new LogEntry { At = DateTime.Now, Tag = "INFO", Text = line };
            }

            DateTime at;
            if (!DateTime.TryParseExact(line.Substring(0, 23), "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out at))
            {
                return new LogEntry { At = DateTime.Now, Tag = "INFO", Text = line };
            }

            var rest = line.Substring(24);
            var tag = "INFO";
            var text = rest;
            if (rest.StartsWith("[", StringComparison.Ordinal))
            {
                var end = rest.IndexOf(']');
                if (end > 1)
                {
                    tag = rest.Substring(1, end - 1);
                    text = end + 2 <= rest.Length ? rest.Substring(end + 2) : "";
                }
            }

            return new LogEntry { At = at, Tag = tag, Text = text };
        }

        public static string ReadLastLines(string logPath, int maxLines)
        {
            if (!File.Exists(logPath) || maxLines <= 0)
            {
                return null;
            }

            string last = null;
            using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8, false))
            {
                string line;
                var ring = new string[maxLines];
                var count = 0;
                var index = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    ring[index] = line;
                    index = (index + 1) % maxLines;
                    if (count < maxLines) count++;
                }

                if (count == 0)
                {
                    return null;
                }

                var sb = new StringBuilder();
                for (var i = 0; i < count; i++)
                {
                    var pos = (index + i) % count;
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append(ring[pos]);
                }

                last = sb.ToString();
            }

            var lines = last.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? null : lines[lines.Length - 1];
        }
    }
}
