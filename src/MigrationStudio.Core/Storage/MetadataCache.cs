using System;
using System.IO;
using System.Text;
using System.Text.Json;
using MigrationStudio.Core.Metadata;

namespace MigrationStudio.Core.Storage
{
    public static class MetadataCache
    {
        public static string PathFor(string dataDirectory, string profileId, string schema)
        {
            var safeProfile = SanitizeFileName(profileId ?? "");
            var safeSchema = SanitizeFileName((schema ?? "").Trim().ToUpperInvariant());
            return Path.Combine(dataDirectory, "metadata", safeProfile + "_" + safeSchema + ".json");
        }

        public static void Save(string dataDirectory, string profileId, SchemaMetadata metadata)
        {
            if (metadata == null)
            {
                throw new ArgumentNullException(nameof(metadata));
            }

            metadata.Cached = false;
            var path = PathFor(dataDirectory, profileId, metadata.Schema);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(metadata, MetadataJsonOptions.Options);
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        public static SchemaMetadata TryLoad(string dataDirectory, string profileId, string schema)
        {
            var path = PathFor(dataDirectory, profileId, schema);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                var meta = JsonSerializer.Deserialize<SchemaMetadata>(json, MetadataJsonOptions.Options);
                if (meta == null)
                {
                    return null;
                }

                meta.Cached = true;
                return meta;
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "_";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
            {
                var bad = false;
                foreach (var c in invalid)
                {
                    if (ch == c)
                    {
                        bad = true;
                        break;
                    }
                }

                sb.Append(bad ? '_' : ch);
            }

            var result = sb.ToString().Trim();
            return result.Length == 0 ? "_" : result;
        }
    }
}
