namespace MigrationStudio.Core.Hosting
{
    public interface ILaunchLog
    {
        void Append(string line);
    }

    public sealed class LaunchLogBuffer : ILaunchLog
    {
        private readonly System.Collections.Generic.Queue<string> _lines = new System.Collections.Generic.Queue<string>();
        private readonly object _gate = new object();
        private const int MaxLines = 50;

        public void Append(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            lock (_gate)
            {
                _lines.Enqueue(line);
                while (_lines.Count > MaxLines)
                {
                    _lines.Dequeue();
                }
            }
        }

        public string Tail()
        {
            lock (_gate)
            {
                return string.Join(System.Environment.NewLine, _lines);
            }
        }
    }
}
