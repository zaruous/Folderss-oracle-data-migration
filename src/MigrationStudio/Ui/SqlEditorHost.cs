using System;
using System.Windows;
using MigrationStudio.Services;

namespace MigrationStudio.Ui
{
    internal sealed class SqlEditorHost
    {
        private SqlSourceEditorWindow _window;
        private readonly Func<IMappingUiHost> _hostFactory;

        public SqlEditorHost(Func<IMappingUiHost> hostFactory)
        {
            _hostFactory = hostFactory;
        }

        public void Open(string mappingId)
        {
            var host = _hostFactory();
            if (host == null || host.Owner == null)
            {
                return;
            }

            if (_window != null && _window.IsLoaded)
            {
                _window.Activate();
                _window.LoadMapping(mappingId);
                return;
            }

            _window = new SqlSourceEditorWindow(host);
            _window.Owner = host.Owner;
            _window.Closed += (s, e) => _window = null;
            _window.LoadMapping(mappingId);
            _window.Show();
        }

        public void Close()
        {
            if (_window != null)
            {
                _window.Close();
                _window = null;
            }
        }

        public bool IsOpen
        {
            get { return _window != null && _window.IsVisible; }
        }

        public SqlSourceEditorWindow Window
        {
            get { return _window; }
        }

        public void PrepareDevHostTab(string tab)
        {
            if (_window == null)
            {
                return;
            }

            _window.PrepareDevHostShot(tab, false);
        }

        public void PrepareDevHostErrorLine()
        {
            if (_window == null)
            {
                return;
            }

            _window.PrepareDevHostShot("check", true);
        }
    }
}
