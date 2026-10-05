using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Oracle.ManagedDataAccess.Client;

namespace MigrationStudio.Core.Adapters
{
    /// <summary>Oracle·ODP.NET 예외를 화면에 보여 줄 한 줄 문장으로 바꾼다(DB Helper DbSession.DescribeError와 같음).</summary>
    public static class OracleErrors
    {
        private const int CancelErrorNumber = 1013;
        private const string CancelErrorCode = "ORA-01013";

        private static readonly HashSet<int> BrokenErrorNumbers = new HashSet<int>
        {
            28, 603, 1012, 1092, 2392, 2396, 2399, 3113, 3114, 3135, 12537, 12547, 12570, 12571
        };

        private static readonly string[] ClosedConnectionPhrases =
        {
            "Connection must be open",
            "Connection is not open",
            "Invalid operation. The connection is closed",
            "연결이 닫혔"
        };

        public static string Describe(Exception ex)
        {
            ex = Unwrap(ex);
            if (ex == null)
            {
                return "알 수 없는 오류가 발생했습니다.";
            }

            if (IsCancellation(ex))
            {
                return "실행을 취소했습니다.";
            }

            if (IsBrokenError(ex))
            {
                var code = BrokenErrorNumber(ex);
                return "DB 연결이 끊겼습니다. 다시 연결하세요." + (code.HasValue ? " (" + OracleCode(code.Value) + ")" : "");
            }

            return WithInnerCause(ex, FirstLine(ex));
        }

        public static string CodeOf(Exception ex)
        {
            string outer = null;
            string innerPreferred = null;
            for (var e = Unwrap(ex); e != null; e = e.InnerException)
            {
                var code = CodeFromException(e);
                if (code == null)
                {
                    continue;
                }

                if (outer == null)
                {
                    outer = code;
                }

                if (!string.Equals(code, "ORA-50201", StringComparison.Ordinal))
                {
                    innerPreferred = code;
                }
            }

            return innerPreferred ?? outer;
        }

        private static string CodeFromException(Exception e)
        {
            var oracle = e as OracleException;
            if (oracle != null && oracle.Number > 0)
            {
                return OracleCode(oracle.Number);
            }

            return ErrorCodeOf(FirstLine(e));
        }

        public static bool IsCancellation(Exception ex)
        {
            for (var e = Unwrap(ex); e != null; e = e.InnerException)
            {
                if (e is OperationCanceledException)
                {
                    return true;
                }

                var oracle = e as OracleException;
                if (oracle != null && oracle.Number == CancelErrorNumber)
                {
                    return true;
                }

                if (e.Message != null && e.Message.IndexOf(CancelErrorCode, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsBrokenError(Exception ex)
        {
            ex = Unwrap(ex);
            if (BrokenErrorNumber(ex).HasValue)
            {
                return true;
            }

            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is InvalidOperationException && MentionsClosedConnection(e.Message))
                {
                    return true;
                }
            }

            return false;
        }

        private static Exception Unwrap(Exception ex)
        {
            for (var i = 0; i < 16 && ex != null; i++)
            {
                var aggregate = ex as AggregateException;
                if (aggregate != null)
                {
                    var inner = aggregate.Flatten().InnerExceptions;
                    if (inner.Count == 0)
                    {
                        return ex;
                    }

                    ex = inner[0];
                }
                else
                {
                    var invocation = ex as TargetInvocationException;
                    if (invocation != null && invocation.InnerException != null)
                    {
                        ex = invocation.InnerException;
                    }
                    else
                    {
                        return ex;
                    }
                }
            }

            return ex;
        }

        private static int? BrokenErrorNumber(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var oracle = e as OracleException;
                if (oracle != null && BrokenErrorNumbers.Contains(oracle.Number))
                {
                    return oracle.Number;
                }
            }

            return null;
        }

        private static bool MentionsClosedConnection(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return false;
            }

            foreach (var phrase in ClosedConnectionPhrases)
            {
                if (message.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string OracleCode(int number)
        {
            return "ORA-" + number.ToString("D5", CultureInfo.InvariantCulture);
        }

        private static string WithInnerCause(Exception ex, string first)
        {
            var outer = ErrorCodeOf(first);
            if (outer == null)
            {
                return first;
            }

            string cause = null;
            var e = ex.InnerException;
            for (var i = 0; i < 16 && e != null; i++, e = e.InnerException)
            {
                var line = FirstLine(e);
                var code = ErrorCodeOf(line);
                if (code != null && code != outer)
                {
                    cause = line;
                }
            }

            return cause == null ? first : cause + " (" + outer + ")";
        }

        private static string ErrorCodeOf(string line)
        {
            if (line == null || line.Length < 9 ||
                !(line.StartsWith("ORA-", StringComparison.Ordinal) || line.StartsWith("TNS-", StringComparison.Ordinal)))
            {
                return null;
            }

            for (var i = 4; i < 9; i++)
            {
                if (line[i] < '0' || line[i] > '9')
                {
                    return null;
                }
            }

            return line.Substring(0, 9);
        }

        private static string FirstLine(Exception ex)
        {
            var lines = new List<string>();
            foreach (var line in (ex.Message ?? "").Split('\n'))
            {
                var text = line.Trim();
                if (text.Length > 0 &&
                    !text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add(text);
                }
            }

            if (lines.Count == 0)
            {
                return ex.GetType().Name;
            }

            var first = lines[0];
            var oracle = ex as OracleException;
            for (var i = 1; i < lines.Count && i < 3 && first.EndsWith(":", StringComparison.Ordinal); i++)
            {
                first += " " + lines[i];
            }

            if (lines.Count > 1 && first == lines[0] &&
                ((oracle != null && oracle.Number == 2091) || first.StartsWith("ORA-02091", StringComparison.Ordinal)))
            {
                first += " " + lines[1];
            }

            if (oracle != null && oracle.Number > 0 && !first.StartsWith("ORA-", StringComparison.Ordinal))
            {
                first = OracleCode(oracle.Number) + ": " + first;
            }

            return first;
        }
    }
}
