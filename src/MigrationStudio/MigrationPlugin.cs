using Folderss.Plugins;
using System.Windows;
using MigrationStudio.Ui;
using MigrationStudio.Ui.Settings;

namespace MigrationStudio
{
    /// <summary>Folderss 플러그인 진입점. plugin.json의 type과 일치해야 한다.</summary>
    public sealed class MigrationPlugin : IFolderssPlugin
    {
        private IPluginManager _manager;

        public void Initialize(IPluginManager manager)
        {
            _manager = manager;
            if (manager != null)
            {
                manager.AddSettingsPage(new MigrationSettingsPage());
            }
        }

        public FrameworkElement CreateView()
        {
            return new MigrationView(_manager);
        }
    }
}
