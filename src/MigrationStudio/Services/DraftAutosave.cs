using System;
using System.Windows.Threading;
using MigrationStudio.Core.Storage;

namespace MigrationStudio.Services
{
    internal static class DraftAutosave
    {
        private static StudioState _owner;
        private static DispatcherTimer _timer;

        public static bool Owns(StudioState state)
        {
            return ReferenceEquals(_owner, state);
        }

        public static void Claim(StudioState state)
        {
            if (_owner != null)
            {
                return;
            }

            _owner = state;
        }

        public static void Release(StudioState state)
        {
            if (!ReferenceEquals(_owner, state))
            {
                return;
            }

            _owner = null;
            StopTimer();
        }

        public static void Schedule(StudioState state)
        {
            if (!ReferenceEquals(_owner, state))
            {
                return;
            }

            if (_timer == null)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _timer.Tick += (s, e) =>
                {
                    _timer.Stop();
                    Flush();
                };
            }

            _timer.Stop();
            _timer.Start();
        }

        public static void FlushNow(StudioState state, string dataDirectory)
        {
            if (!ReferenceEquals(_owner, state) || state == null)
            {
                return;
            }

            Save(state, dataDirectory);
        }

        private static void Flush()
        {
            if (_owner == null)
            {
                return;
            }

            Save(_owner, AppServices.DataDirectory);
        }

        private static void Save(StudioState state, string dataDirectory)
        {
            if (string.IsNullOrEmpty(dataDirectory) || state == null)
            {
                return;
            }

            try
            {
                var draft = new JobDraft
                {
                    Job = state.Job,
                    FilePath = state.FilePath,
                    Dirty = state.Dirty
                };
                JobDraftStore.Save(dataDirectory, draft);
            }
            catch (Exception)
            {
            }
        }

        private static void StopTimer()
        {
            if (_timer != null)
            {
                _timer.Stop();
            }
        }
    }
}
