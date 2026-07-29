using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private readonly HashSet<int> _emittedCommentPositions = [];

    private void EmitLeadingComments(SyntaxNode node) => EmitCommentTrivia(node.GetLeadingTrivia());

    private void EmitLeadingComments(IrSource source) => EmitIrComments(source.LeadingComments);

    private void EmitTrailingComments(SyntaxNode node) => EmitCommentTrivia(node.GetTrailingTrivia());

    private void EmitTrailingComments(IrSource source) => EmitIrComments(source.TrailingComments);

    private void EmitExpressionComments(ExpressionSyntax expression) =>
        EmitCommentTrivia(expression.DescendantTrivia(descendIntoTrivia: true));

    private void EmitExpressionComments(IrExpression expression) =>
        EmitIrComments(expression.Source.DescendantComments);

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

    private void EmitIrComments(IEnumerable<IrComment> comments)
    {
        foreach (var comment in comments)
        {
            if (!_emittedCommentPositions.Add(comment.Start))
                continue;
            EmitSqlComment(comment);
        }
    }

    private void EmitSqlComment(IrComment comment)
    {
        if (comment.Kind == IrCommentKind.Line)
        {
            _sql.Line("--" + comment.Text[2..]);
            return;
        }
        if (comment.Kind == IrCommentKind.Documentation)
        {
            foreach (var line in SplitLines(comment.Text))
            {
                var trimmed = line.TrimStart();
                _sql.Line("-- " + (trimmed.StartsWith("///", StringComparison.Ordinal) ? trimmed[3..].TrimStart() : trimmed));
            }
            return;
        }
        foreach (var line in SplitLines(comment.Text))
            _sql.Line(line.TrimEnd());
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
