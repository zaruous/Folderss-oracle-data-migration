using System.Windows;
using Folderss.Plugins;
using MigrationStudio.Core.Model;

namespace MigrationStudio.Ui.Settings
{
    public sealed class MigrationSettingsPage : IPluginSettingsPage
    {
        private MigrationSettingsView _view;

        public string Title
        {
            get { return "Migration Studio"; }
        }

        public FrameworkElement CreateView()
        {
            _view = new MigrationSettingsView(null);
            _view.LoadFromRepository();
            return _view;
        }

        public void Save()
        {
            if (_view == null || !_view.Commit())
            {
                throw new System.InvalidOperationException("마이그레이션 설정을 저장하지 못했습니다.");
            }
        }
    }
}
