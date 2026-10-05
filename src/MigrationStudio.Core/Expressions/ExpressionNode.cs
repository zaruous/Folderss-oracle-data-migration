using System.Collections.Generic;

namespace MigrationStudio.Core.Expressions
{
    /// <summary>POC expression.js AST 노드.</summary>
    public sealed class ExpressionNode
    {
        public string Kind;
        public object Value;
        public bool IsString;
        public string Name;
        public string Op;
        public string TypeName;
        public bool Negated;
        public bool Timestamp;
        public int Position;
        public ExpressionNode Left;
        public ExpressionNode Right;
        public ExpressionNode Operand;
        public ExpressionNode Pattern;
        public ExpressionNode Low;
        public ExpressionNode High;
        public ExpressionNode Subject;
        public ExpressionNode Else;
        public List<ExpressionNode> Args;
        public List<ExpressionNode> Items;
        public List<CaseWhen> Whens;
    }
}
