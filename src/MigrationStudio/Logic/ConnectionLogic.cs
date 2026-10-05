using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;

namespace MigrationStudio.Logic
{
    public enum ConnectionNoticeKind
    {
        None,
        MissingProfile,
        TargetWriteBlocked,
        TargetRedColor
    }

    public static class ConnectionLogic
    {
        public static string ComboItemText(ConnectionProfile profile)
        {
            if (profile == null)
            {
                return Labels.ComboPlaceholder;
            }

            var host = (profile.Host ?? "") + "/" + (profile.Service ?? "");
            var text = profile.Name + "   " + host;
            if (profile.WriteBlocked)
            {
                text += " (쓰기 금지)";
            }

            return text;
        }

        public static ConnectionNoticeKind NoticeKind(string role, ConnectionProfile profile, ConnectionRef missing)
        {
            if (missing != null)
            {
                return ConnectionNoticeKind.MissingProfile;
            }

            if (profile == null)
            {
                return ConnectionNoticeKind.None;
            }

            if (role == Roles.Target && profile.WriteBlocked)
            {
                return ConnectionNoticeKind.TargetWriteBlocked;
            }

            if (role == Roles.Target && string.Equals(profile.Color, "red", StringComparison.OrdinalIgnoreCase))
            {
                return ConnectionNoticeKind.TargetRedColor;
            }

            return ConnectionNoticeKind.None;
        }

        public static string TestStatusLine(ConnectionSession session, ConnectionTestResult result)
        {
            if (session == null)
            {
                return "";
            }

            if (session.Status == ConnStatus.Testing)
            {
                return "testing";
            }

            if (session.Status == ConnStatus.Ok && result != null && result.Ok)
            {
                var ms = result.LatencyMs.HasValue ? result.LatencyMs.Value.ToString(CultureInfo.InvariantCulture) : "0";
                return "ok|✓ Connected|" + (result.Version ?? "") + "|" + ms + "|" + FormatTime(result.TestedAt);
            }

            if (session.Status == ConnStatus.Error && result != null && !result.Ok)
            {
                return "error|" + (result.Error ?? "");
            }

            return "unknown";
        }

        /// <summary>메타데이터 상태 한 줄(아이콘 제외). 캐시·시각 괄호는 UI에서 DisabledText로 칠한다.</summary>
        public static string MetaStatusLine(SchemaMetadata meta, ConnectionSession session, bool loading)
        {
            if (loading || (session != null && session.MetaLoading))
            {
                return "loading";
            }

            if (session != null && !string.IsNullOrEmpty(session.MetaError))
            {
                return "error|" + session.MetaError;
            }

            if (meta == null)
            {
                return "none";
            }

            var tables = meta.Tables != null ? meta.Tables.Count : 0;
            var views = meta.Tables != null ? meta.Tables.FindAll(t => string.Equals(t.Kind, "VIEW", StringComparison.OrdinalIgnoreCase)).Count : 0;
            var tableCount = tables - views;
            var columns = 0;
            if (meta.Tables != null)
            {
                foreach (var t in meta.Tables)
                {
                    columns += t.Columns != null ? t.Columns.Count : 0;
                }
            }

            var head = meta.Schema + " · 테이블 " + tableCount.ToString(CultureInfo.InvariantCulture) + " · 뷰 " + views.ToString(CultureInfo.InvariantCulture) + " · 컬럼 " + columns.ToString(CultureInfo.InvariantCulture);
            if (meta.Cached)
            {
                return head + " (캐시 · " + FormatTime(meta.LoadedAt) + ")";
            }

            return head + " (" + FormatTime(meta.LoadedAt) + " · " + meta.ElapsedMs.ToString(CultureInfo.InvariantCulture) + " ms)";
        }

        public static string Address(ConnectionProfile profile)
        {
            if (profile == null)
            {
                return "";
            }

            return (profile.Host ?? "") + ":" + profile.Port.ToString(CultureInfo.InvariantCulture) + "/" + (profile.Service ?? "");
        }

        public static string PasswordSummary(ConnectionProfile profile, bool canUnprotect)
        {
            if (profile == null)
            {
                return "";
            }

            if (!string.IsNullOrEmpty(profile.ProtectedPassword) && profile.SavePassword)
            {
                return canUnprotect ? "stored" : "locked";
            }

            return "prompt";
        }

        public static List<ConnectionProfile> ComboProfiles(MigrationSettings settings, string role)
        {
            var list = new List<ConnectionProfile>();
            if (settings == null || settings.Connections == null)
            {
                return list;
            }

            foreach (var c in settings.Connections)
            {
                if (role == Roles.Target && c.WriteBlocked)
                {
                    continue;
                }

                list.Add(c);
            }

            return list;
        }

        private static string FormatTime(DateTime value)
        {
            return value.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }
}
