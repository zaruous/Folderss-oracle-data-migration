using System;
using System.Security.Cryptography;
using System.Text;

namespace MigrationStudio.Services
{
    internal static class PasswordProtector
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Folderss.Migration.v1");

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain))
            {
                return null;
            }

            var bytes = Encoding.UTF8.GetBytes(plain);
            var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        public static bool TryUnprotect(string protectedValue, out string plain)
        {
            plain = null;
            if (string.IsNullOrEmpty(protectedValue))
            {
                return true;
            }

            try
            {
                var bytes = Convert.FromBase64String(protectedValue);
                var plainBytes = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
                plain = Encoding.UTF8.GetString(plainBytes);
                return true;
            }
            catch (CryptographicException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
