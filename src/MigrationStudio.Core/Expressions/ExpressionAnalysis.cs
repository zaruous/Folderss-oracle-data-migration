using System.Collections.Generic;

namespace MigrationStudio.Core.Expressions
{
    public sealed class ExpressionAnalysis
    {
        internal ExpressionAnalysis(
            ExpressionNode node,
            IReadOnlyList<string> refs,
            IReadOnlyList<string> binds,
            SqlParseException error)
        {
            Node = node;
            Refs = refs;
            Binds = binds;
            Error = error;
        }

        public ExpressionNode Node { get; }

        public IReadOnlyList<string> Refs { get; }

        public IReadOnlyList<string> Binds { get; }

        public SqlParseException Error { get; }
    }
}
