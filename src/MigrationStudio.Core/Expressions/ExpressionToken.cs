namespace MigrationStudio.Core.Expressions
{
    internal sealed class ExpressionToken
    {
        internal string Type;
        internal object Value;
        internal int At;
        internal string Raw;
        internal bool Quoted;
    }
}
