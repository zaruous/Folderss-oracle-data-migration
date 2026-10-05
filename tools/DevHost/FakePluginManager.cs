using System;
using System.Collections.Generic;
using System.IO;
using Folderss.Plugins;

namespace DevHost
{
    internal sealed class FakePluginManager : IPluginManager
    {
        private readonly string _dataDir;
        private readonly string _pluginDir;
        private readonly Dictionary<string, string> _settings = new Dictionary<string, string>(StringComparer.Ordinal);

        public FakePluginManager(string dataDir, string pluginDir)
        {
            _dataDir = dataDir;
            _pluginDir = pluginDir;
            Directory.CreateDirectory(_dataDir);
        }

        public string PluginId
        {
            get { return "zaruous.folderss-oracle-migration"; }
        }

        public string DataDirectory
        {
            get { return _dataDir; }
        }

        public string PluginDirectory
        {
            get { return _pluginDir; }
        }

        public string GetSetting(string key)
        {
            string value;
            return _settings.TryGetValue(key, out value) ? value : null;
        }

        public void SetSetting(string key, string value)
        {
            if (value == null)
            {
                _settings.Remove(key);
            }
            else
            {
                _settings[key] = value;
            }
        }

        public IReadOnlyDictionary<string, string> GetAllSettings()
        {
            return new Dictionary<string, string>(_settings);
        }

        public IReadOnlyDictionary<string, string> GetAppSettings()
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public IFolderPanel CreateFolderPanel(string path)
        {
            throw new NotSupportedException();
        }

        public void AddSettingsPage(IPluginSettingsPage page)
        {
        }

        public void LoadSettingsFile(string path)
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                _settings[MigrationStudio.Core.Settings.MigrationSettingsStore.SettingKey] = json;
            }
        }

        public IReadOnlyList<IPluginSettingsPage> SettingsPages { get; private set; } = new List<IPluginSettingsPage>();

        public void RegisterSettingsPage(IPluginSettingsPage page)
        {
            var list = new List<IPluginSettingsPage>((List<IPluginSettingsPage>)SettingsPages) { page };
            SettingsPages = list;
        }
    }
}
