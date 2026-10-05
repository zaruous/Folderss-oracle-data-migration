using System;

namespace MigrationStudio.Core.Expressions
{
    /// <summary>식 해석 오류(POC OraError와 같은 메시지 형식).</summary>
    public sealed class SqlParseException : Exception
    {
        public SqlParseException(string code, string message, int position)
            : base(code + ": " + message)
        {
            Code = code;
            Position = position;
        }

        public string Code { get; }

        /// <summary>원문 기준 0부터; 알 수 없으면 -1.</summary>
        public int Position { get; }
    }
}
