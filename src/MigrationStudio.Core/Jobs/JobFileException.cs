using System;

namespace MigrationStudio.Core.Jobs
{
    /// <summary>작업·매핑 템플릿 파일 형식 오류 또는 JSON 구문 오류.</summary>
    public sealed class JobFileException : Exception
    {
        public JobFileException(string message)
            : base(message)
        {
        }

        public JobFileException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
