using System;
using System.Globalization;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;

namespace MigrationStudio.Logic
{
    public static class JobLogic
    {
        public static MigrationJob NewJob(MigrationSettings settings)
        {
            var job = new MigrationJob
            {
                JobName = "NEW_MIGRATION",
                Description = "",
                Source = new ConnectionRef(),
                Target = new ConnectionRef(),
                Strategy = new MigrationStrategy()
            };
            if (settings != null && settings.Defaults != null)
            {
                MigrationSettingsStore.ApplyDefaults(job.Strategy, settings.Defaults);
            }

            return job;
        }

        /// <summary>profileId가 없으면 이름(대소문자 무시)으로 찾아 넣는다. 못 찾으면 사본 그대로.</summary>
        public static bool ResolveConnections(MigrationJob job, MigrationSettings settings)
        {
            if (job == null || settings == null)
            {
                return false;
            }

            var changed = false;
            changed |= ResolveRole(job, settings, Roles.Source, job.Source);
            changed |= ResolveRole(job, settings, Roles.Target, job.Target);
            return changed;
        }

        private static bool ResolveRole(MigrationJob job, MigrationSettings settings, string role, ConnectionRef reference)
        {
            if (reference == null)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(reference.ProfileId) && MigrationSettingsStore.Find(settings, reference.ProfileId) != null)
            {
                return false;
            }

            if (string.IsNullOrEmpty(reference.Name))
            {
                return false;
            }

            var byName = MigrationSettingsStore.FindByName(settings, reference.Name);
            if (byName == null)
            {
                return false;
            }

            reference.ProfileId = byName.Id;
            return true;
        }

        public static ConnectionRef Missing(MigrationJob job, MigrationSettings settings, string role)
        {
            if (job == null)
            {
                return null;
            }

            var reference = role == Roles.Source ? job.Source : job.Target;
            if (reference == null || string.IsNullOrEmpty(reference.ProfileId))
            {
                return string.IsNullOrEmpty(reference != null ? reference.Name : null) ? null : reference;
            }

            if (MigrationSettingsStore.Find(settings, reference.ProfileId) != null)
            {
                return null;
            }

            return reference;
        }

        public static ConnectionProfile ProfileFromMissing(ConnectionRef reference, MigrationSettings settings)
        {
            if (reference == null)
            {
                return null;
            }

            var name = reference.Name ?? "MISSING";
            var unique = name;
            var n = 2;
            while (MigrationSettingsStore.FindByName(settings, unique) != null)
            {
                unique = name + "_" + n.ToString(CultureInfo.InvariantCulture);
                n++;
            }

            var profile = MigrationSettingsStore.NewProfile(settings);
            profile.Name = unique;
            profile.Kind = string.IsNullOrEmpty(reference.Kind) ? "oracle" : reference.Kind;
            profile.Color = reference.Color ?? "";
            profile.Host = reference.Host ?? "";
            profile.Port = ParsePort(reference.Port);
            profile.Service = reference.Service ?? "";
            profile.User = reference.User ?? "";
            profile.DefaultSchema = reference.Schema ?? "";
            profile.SavePassword = false;
            profile.WriteBlocked = false;
            return profile;
        }

        public static void ApplyProfileSelection(MigrationJob job, string role, ConnectionProfile profile)
        {
            if (job == null || profile == null)
            {
                return;
            }

            var reference = role == Roles.Source ? job.Source : job.Target;
            if (reference == null)
            {
                return;
            }

            reference.ProfileId = profile.Id;
            var schema = profile.DefaultSchema;
            if (string.IsNullOrWhiteSpace(schema))
            {
                schema = profile.User;
            }

            reference.Schema = schema ?? "";
        }

        public static string DefaultSchemaPlaceholder(ConnectionProfile profile)
        {
            if (profile == null)
            {
                return "";
            }

            if (!string.IsNullOrWhiteSpace(profile.DefaultSchema))
            {
                return profile.DefaultSchema.Trim().ToUpperInvariant();
            }

            return string.IsNullOrWhiteSpace(profile.User) ? "" : profile.User.Trim().ToUpperInvariant();
        }

        private static int ParsePort(string port)
        {
            int value;
            if (int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }

            return 1521;
        }
    }
}
