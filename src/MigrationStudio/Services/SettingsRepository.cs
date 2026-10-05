using System;
using System.Threading;
using Folderss.Plugins;
using MigrationStudio.Core.Settings;

namespace MigrationStudio.Services
{
    public sealed class SettingsConflictException : Exception
    {
        public SettingsConflictException(string message) : base(message)
        {
        }
    }

    public sealed class LoadedSettings
    {
        public MigrationSettings Settings { get; set; }
        public string Revision { get; set; }
        public string Error { get; set; }
    }

    internal static class SettingsRepository
    {
        public static event Action Changed;

        public static LoadedSettings Load(IPluginManager manager)
        {
            var raw = manager != null ? manager.GetSetting(MigrationSettingsStore.SettingKey) : null;
            var loaded = new LoadedSettings();
            if (string.IsNullOrWhiteSpace(raw))
            {
                loaded.Settings = MigrationSettingsStore.CreateDefaultSettings();
                loaded.Revision = "";
                return loaded;
            }

            loaded.Revision = raw;
            try
            {
                loaded.Settings = MigrationSettingsStore.Deserialize(raw);
            }
            catch (Exception ex)
            {
                loaded.Settings = MigrationSettingsStore.CreateDefaultSettings();
                loaded.Error = "설정 JSON을 읽지 못했습니다: " + ex.Message + " — 저장하면 기본값으로 덮어씁니다.";
            }

            return loaded;
        }

        public static void Save(IPluginManager manager, MigrationSettings settings, string revision)
        {
            if (manager == null)
            {
                throw new ArgumentNullException(nameof(manager));
            }

            var current = manager.GetSetting(MigrationSettingsStore.SettingKey) ?? "";
            if (!string.Equals(current, revision ?? "", StringComparison.Ordinal))
            {
                throw new SettingsConflictException("다른 창에서 설정을 먼저 저장했습니다. 이 창의 변경은 저장하지 않았습니다 — 설정을 다시 열어 고치세요.");
            }

            var json = MigrationSettingsStore.Serialize(settings);
            manager.SetSetting(MigrationSettingsStore.SettingKey, json);
            NotifySubscribers();
        }

        private static void NotifySubscribers()
        {
            var handler = Changed;
            if (handler == null)
            {
                return;
            }

            foreach (var del in handler.GetInvocationList())
            {
                try
                {
                    ((Action)del).Invoke();
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
