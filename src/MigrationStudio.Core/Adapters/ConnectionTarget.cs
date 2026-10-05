using System;
using System.Globalization;
using MigrationStudio.Core.Settings;

namespace MigrationStudio.Core.Adapters
{
    /// <summary>연결에 필요한 값. 비밀번호 평문은 메모리에만 — 파일·로그·예외 메시지에 넣지 않는다.</summary>
    public sealed class ConnectionTarget
    {
        public string Kind { get; set; } = "oracle";
        public string Host { get; set; }
        public int Port { get; set; } = 1521;
        public string Service { get; set; }
        public string User { get; set; }
        public string Password { get; set; }

        public static ConnectionTarget From(ConnectionProfile profile, string password)
        {
            if (profile == null)
            {
                return null;
            }

            return new ConnectionTarget
            {
                Kind = string.IsNullOrEmpty(profile.Kind) ? "oracle" : profile.Kind,
                Host = profile.Host,
                Port = profile.Port,
                Service = profile.Service,
                User = profile.User,
                Password = password
            };
        }

        public string Describe()
        {
            var user = User ?? "";
            var host = Host ?? "";
            var service = Service ?? "";
            return user + "@" + host + ":" + Port.ToString(CultureInfo.InvariantCulture) + "/" + service;
        }

        public override string ToString()
        {
            return Describe();
        }

        /// <summary>연결 전 검사. 실패 시 사람이 읽을 문장(비밀번호 제외).</summary>
        internal static string Validate(ConnectionTarget target)
        {
            if (target == null)
            {
                return "접속 정보가 없습니다";
            }

            if (string.IsNullOrWhiteSpace(target.Password))
            {
                return "비밀번호를 입력하세요";
            }

            var profile = new ConnectionProfile
            {
                Name = "validate",
                Host = target.Host,
                Port = target.Port,
                Service = target.Service,
                User = target.User
            };
            var errors = MigrationSettingsStore.ValidateProfile(profile, new[] { profile });
            if (errors.Count == 0)
            {
                return null;
            }

            return string.Join(" · ", errors);
        }
    }
}
