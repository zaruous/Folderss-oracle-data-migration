namespace MigrationStudio.Core.Hosting
{
    public interface IPluginDirs
    {
        string PluginDirectory { get; }
        string DataDirectory { get; }
    }

    public sealed class PluginDirs : IPluginDirs
    {
        public PluginDirs(string pluginDirectory, string dataDirectory)
        {
            PluginDirectory = pluginDirectory;
            DataDirectory = dataDirectory;
        }

        public string PluginDirectory { get; private set; }
        public string DataDirectory { get; private set; }
    }
}
