using System;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MigrationStudio.Core.Hosting
{
    public static class AgentPipeSecurity
    {
        [SupportedOSPlatform("windows")]
        public static PipeSecurity CurrentUserOnly()
        {
            var security = new PipeSecurity();
            var user = WindowsIdentity.GetCurrent().User;
            if (user == null)
            {
                throw new InvalidOperationException("현재 Windows 사용자 SID를 알 수 없습니다.");
            }

            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.SetAccessRuleProtection(true, false);
            return security;
        }
    }
}
