using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MigrationStudio.Core.Engine
{
    public static class AgentMessageCodec
    {
        public static byte[] Serialize(AgentMessage message)
        {
            var json = JsonSerializer.Serialize(message, AgentProtocol.JsonOptions);
            if (json.IndexOf('\n') >= 0 || json.IndexOf('\r') >= 0)
            {
                throw new InvalidOperationException("메시지 JSON에 개행이 들어갈 수 없습니다.");
            }

            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            if (bytes.Length > AgentProtocol.MaxMessageBytes)
            {
                throw new InvalidOperationException("메시지가 8MB를 초과합니다.");
            }

            return bytes;
        }

        public static AgentMessage Deserialize(string line)
        {
            if (line == null)
            {
                return null;
            }

            if (Encoding.UTF8.GetByteCount(line) > AgentProtocol.MaxMessageBytes)
            {
                throw new InvalidOperationException("메시지가 8MB를 초과합니다.");
            }

            return JsonSerializer.Deserialize<AgentMessage>(line, AgentProtocol.JsonOptions);
        }

        public static bool TryReadLine(Stream stream, StringBuilder buffer, out string line)
        {
            line = null;
            var chunk = new byte[4096];
            while (true)
            {
                var at = buffer.ToString();
                var nl = at.IndexOf('\n');
                if (nl >= 0)
                {
                    line = at.Substring(0, nl).TrimEnd('\r');
                    buffer.Clear();
                    if (nl + 1 < at.Length)
                    {
                        buffer.Append(at.Substring(nl + 1));
                    }

                    return true;
                }

                var read = stream.Read(chunk, 0, chunk.Length);
                if (read <= 0)
                {
                    if (buffer.Length > 0)
                    {
                        line = buffer.ToString().TrimEnd('\r', '\n');
                        buffer.Clear();
                        return line.Length > 0;
                    }

                    return false;
                }

                buffer.Append(Encoding.UTF8.GetString(chunk, 0, read));
                if (buffer.Length > AgentProtocol.MaxMessageBytes + 1)
                {
                    throw new InvalidOperationException("메시지가 8MB를 초과합니다.");
                }
            }
        }
    }
}
