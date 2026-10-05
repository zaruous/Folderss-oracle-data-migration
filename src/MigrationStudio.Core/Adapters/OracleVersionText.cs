using System;
using System.Globalization;

namespace MigrationStudio.Core.Adapters
{
    public static class OracleVersionText
    {
        public static string FromServerVersion(string serverVersion)
        {
            if (string.IsNullOrWhiteSpace(serverVersion))
            {
                return "Oracle";
            }

            var parts = serverVersion.Split('.');
            if (parts.Length == 0)
            {
                return "Oracle " + serverVersion;
            }

            int major;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out major))
            {
                return "Oracle " + serverVersion;
            }

            if (major >= 23)
            {
                return "Oracle 23ai";
            }

            switch (major)
            {
                case 11:
                    return "Oracle 11g";
                case 12:
                    return "Oracle 12c";
                case 18:
                    return "Oracle 18c";
                case 19:
                    return "Oracle 19c";
                case 21:
                    return "Oracle 21c";
                default:
                    return "Oracle " + serverVersion;
            }
        }
    }
}
