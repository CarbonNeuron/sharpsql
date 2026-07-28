using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private readonly HashSet<int> _emittedCommentPositions = [];

    private void EmitLeadingComments(SyntaxNode node) => EmitCommentTrivia(node.GetLeadingTrivia());

    private void EmitTrailingComments(SyntaxNode node) => EmitCommentTrivia(node.GetTrailingTrivia());

    private void EmitExpressionComments(ExpressionSyntax expression) =>
        EmitCommentTrivia(expression.DescendantTrivia(descendIntoTrivia: true));

    private void EmitFileHeaderComments(CompilationUnitSyntax root)
    {
        var firstMemberStart = root.Members.FirstOrDefault()?.SpanStart ?? root.EndOfFileToken.SpanStart;
        EmitCommentTrivia(root.DescendantTrivia(descendIntoTrivia: true)
            .Where(trivia => trivia.SpanStart < firstMemberStart));
    }

    private void EmitAllRemainingComments(CompilationUnitSyntax root) =>
        EmitCommentTrivia(root.DescendantTrivia(descendIntoTrivia: true));

    private void EmitCommentTrivia(IEnumerable<SyntaxTrivia> triviaItems)
    {
        foreach (var trivia in triviaItems)
        {
            if (!IsComment(trivia) || !_emittedCommentPositions.Add(trivia.SpanStart))
                continue;
            EmitSqlComment(trivia);
        }
    }

    private void EmitSqlComment(SyntaxTrivia trivia)
    {
        var text = trivia.ToFullString().TrimEnd('\r', '\n');
        if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia))
        {
            _sql.Line("--" + text[2..]);
            return;
        }
        if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
        {
            foreach (var line in SplitLines(text))
            {
                var trimmed = line.TrimStart();
                _sql.Line("-- " + (trimmed.StartsWith("///", StringComparison.Ordinal) ? trimmed[3..].TrimStart() : trimmed));
            }
            return;
        }

        foreach (var line in SplitLines(text))
            _sql.Line(line.TrimEnd());
    }

    private static bool IsComment(SyntaxTrivia trivia) => trivia.Kind() is
        SyntaxKind.SingleLineCommentTrivia or
        SyntaxKind.MultiLineCommentTrivia or
        SyntaxKind.SingleLineDocumentationCommentTrivia or
        SyntaxKind.MultiLineDocumentationCommentTrivia;

    private static IEnumerable<string> SplitLines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
}
