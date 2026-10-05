using System;
using System.Collections.Generic;
using System.Globalization;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;

namespace MigrationStudio.Logic
{
    public static class SettingsLogic
    {
        public static ConnectionProfile AddProfile(MigrationSettings settings)
        {
            var profile = MigrationSettingsStore.NewProfile(settings);
            settings.Connections.Add(profile);
            return profile;
        }

        public static ConnectionProfile DuplicateProfile(MigrationSettings settings, ConnectionProfile source)
        {
            if (source == null)
            {
                return null;
            }

            var copy = MigrationSettingsStore.NewProfile(settings);
            copy.Name = source.Name + "_COPY";
            copy.Kind = source.Kind;
            copy.Color = source.Color;
            copy.Host = source.Host;
            copy.Port = source.Port;
            copy.Service = source.Service;
            copy.User = source.User;
            copy.DefaultSchema = source.DefaultSchema;
            copy.WriteBlocked = source.WriteBlocked;
            copy.SavePassword = false;
            copy.ProtectedPassword = null;
            copy.LegacyPassword = null;
            settings.Connections.Add(copy);
            return copy;
        }

        public static bool CanDeleteProfile(MigrationJob job, ConnectionProfile profile)
        {
            if (profile == null || job == null)
            {
                return true;
            }

            if (job.Source != null && string.Equals(job.Source.ProfileId, profile.Id, StringComparison.Ordinal))
            {
                return false;
            }

            if (job.Target != null && string.Equals(job.Target.ProfileId, profile.Id, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        /// <summary>검사 실패 시 고를 접속 id, 성공 시 null.</summary>
        public static string ValidateAll(MigrationSettings settings, out string firstError)
        {
            firstError = null;
            if (settings == null || settings.Connections == null)
            {
                return null;
            }

            foreach (var profile in settings.Connections)
            {
                var errors = MigrationSettingsStore.ValidateProfile(profile, settings.Connections);
                if (errors.Count > 0)
                {
                    firstError = string.Join(" · ", errors);
                    return profile.Id;
                }
            }

            return null;
        }

        public static string RoleLabelForProfile(MigrationJob job, string profileId)
        {
            if (job == null || string.IsNullOrEmpty(profileId))
            {
                return "";
            }

            var parts = new List<string>();
            if (job.Source != null && string.Equals(job.Source.ProfileId, profileId, StringComparison.Ordinal))
            {
                parts.Add("원본");
            }

            if (job.Target != null && string.Equals(job.Target.ProfileId, profileId, StringComparison.Ordinal))
            {
                parts.Add("대상");
            }

            return parts.Count == 0 ? "" : string.Join("·", parts.ToArray());
        }
    }
}
