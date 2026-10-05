using MigrationStudio.Core.Settings;

namespace MigrationStudio.Core.Hosting
{
    /// <summary>접속 프로필별 비밀번호. null이면 사용자가 취소한 것으로 간주한다.</summary>
    public delegate string PasswordResolver(ConnectionProfile profile, string reason);
}
