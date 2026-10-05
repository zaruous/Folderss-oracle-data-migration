using System;

namespace MigrationStudio.Core.Adapters
{
    public sealed class AdapterException : Exception
    {
        public string Code { get; }

        public AdapterException(Exception inner)
            : base(OracleErrors.Describe(inner), inner)
        {
            Code = OracleErrors.CodeOf(inner);
        }

        public AdapterException(string message, Exception inner)
            : base(OracleErrors.Describe(inner ?? new Exception(message)), inner)
        {
            Code = inner != null ? OracleErrors.CodeOf(inner) : null;
        }
    }
}
