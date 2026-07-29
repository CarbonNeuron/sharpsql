namespace SharpSql;

internal enum AsyncAwaitOperationKind
{
    Task,
    Delay,
    WhenAll
}

internal sealed record AsyncSuspensionPoint(
    int ResumeState,
    AsyncAwaitOperationKind Operation,
    IrAwaitExpression AwaitExpression,
    IReadOnlyList<IrSymbol> LiveSymbols);

/// <summary>
/// Backend-neutral suspension metadata. SQL emission can use this plan to decide which
/// locals must be written to a durable task payload before returning an activated reader.
/// </summary>
internal sealed record AsyncStateMachinePlan(
    string HandlerName,
    IrMethodId MethodId,
    IReadOnlyList<AsyncSuspensionPoint> SuspensionPoints)
{
    public int StateCount => SuspensionPoints.Count + 1;

    public static AsyncStateMachinePlan Create(string handlerName, MethodDefinition method) =>
        Create(handlerName, method.Id, method.Body, method.ExpressionBody);

    public static AsyncStateMachinePlan Create(string handlerName, ProceduralBlock entryPoint) =>
        Create(handlerName, IrMethodId.None, entryPoint, expressionBody: null);

    private static AsyncStateMachinePlan Create(
        string handlerName,
        IrMethodId methodId,
        ProceduralBlock? body,
        IrExpression? expressionBody)
    {
        var events = new List<PlanEvent>();
        if (body is not null)
            VisitStatement(body, events);
        if (expressionBody is not null)
            VisitExpression(expressionBody, events);

        var points = new List<AsyncSuspensionPoint>();
        for (var index = 0; index < events.Count; index++)
        {
            if (events[index].AwaitExpression is not { } awaitExpression)
                continue;

            var live = events.Skip(index + 1)
                .Where(item => item.Symbol is not null)
                .Select(item => item.Symbol!)
                .GroupBy(item => item.Id)
                .Select(group => group.First())
                .ToArray();
            points.Add(new AsyncSuspensionPoint(
                points.Count + 1,
                AwaitKind(awaitExpression),
                awaitExpression,
                live));
        }

        return new AsyncStateMachinePlan(handlerName, methodId, points);
    }

    private static AsyncAwaitOperationKind AwaitKind(IrAwaitExpression expression) =>
        expression.Operand is IrInvocationExpression delay && IsTaskMethod(delay, "Delay")
            ? AsyncAwaitOperationKind.Delay
            : expression.Operand is IrInvocationExpression whenAll && IsTaskMethod(whenAll, "WhenAll")
                ? AsyncAwaitOperationKind.WhenAll
                : AsyncAwaitOperationKind.Task;

    private static bool IsTaskMethod(IrInvocationExpression invocation, string methodName)
    {
        if (!invocation.TargetMethodId.IsNone)
        {
            return invocation.TargetMethodId.Value.StartsWith(
                $"M:System.Threading.Tasks.Task.{methodName}",
                StringComparison.Ordinal);
        }

        // Raw-source transpilation intentionally tolerates missing convenience global
        // usings. Keep that path precise enough not to classify a direct user method
        // named Delay/WhenAll as a Task intrinsic.
        return invocation.Target is IrMemberExpression
        {
            MemberName: var memberName,
            Receiver: IrVariableExpression { Symbol.Name: "Task" }
        } && string.Equals(memberName, methodName, StringComparison.Ordinal);
    }

    private static void VisitStatement(ProceduralStatement statement, List<PlanEvent> events)
    {
        switch (statement)
        {
            case ProceduralBlock block:
                foreach (var child in block.Statements)
                    VisitStatement(child, events);
                break;
            case ProceduralDeclarationStatement declaration:
                foreach (var variable in declaration.Declaration.Variables)
                    if (variable.Initializer is not null)
                        VisitExpression(variable.Initializer, events);
                break;
            case ProceduralExpressionStatement expression:
                VisitExpression(expression.Expression, events);
                break;
            case ProceduralIf @if:
                VisitExpression(@if.Condition, events);
                VisitStatement(@if.Then, events);
                if (@if.Else is not null)
                    VisitStatement(@if.Else, events);
                break;
            case ProceduralWhile @while:
                VisitExpression(@while.Condition, events);
                VisitStatement(@while.Body, events);
                break;
            case ProceduralDo @do:
                VisitStatement(@do.Body, events);
                VisitExpression(@do.Condition, events);
                break;
            case ProceduralFor @for:
                if (@for.Declaration is not null)
                    foreach (var variable in @for.Declaration.Variables)
                        if (variable.Initializer is not null)
                            VisitExpression(variable.Initializer, events);
                foreach (var initializer in @for.Initializers)
                    VisitExpression(initializer, events);
                if (@for.Condition is not null)
                    VisitExpression(@for.Condition, events);
                VisitStatement(@for.Body, events);
                foreach (var incrementor in @for.Incrementors)
                    VisitExpression(incrementor, events);
                break;
            case ProceduralForEach forEach:
                VisitExpression(forEach.SourceExpression, events);
                VisitStatement(forEach.Body, events);
                break;
            case ProceduralTry @try:
                VisitStatement(@try.Body, events);
                foreach (var @catch in @try.Catches)
                {
                    if (@catch.Filter is not null)
                        VisitExpression(@catch.Filter, events);
                    VisitStatement(@catch.Body, events);
                }
                break;
            case ProceduralThrow { Expression: not null } @throw:
                VisitExpression(@throw.Expression, events);
                break;
            case ProceduralReturn { Expression: not null } @return:
                VisitExpression(@return.Expression, events);
                break;
        }
    }

    private static void VisitExpression(IrExpression expression, List<PlanEvent> events)
    {
        if (expression is IrAwaitExpression awaitExpression)
        {
            VisitExpression(awaitExpression.Operand, events);
            events.Add(new PlanEvent(awaitExpression, null));
            return;
        }

        if (expression is IrVariableExpression variable)
        {
            events.Add(new PlanEvent(null, variable.Symbol));
            return;
        }

        switch (expression)
        {
            case IrBinaryExpression binary:
                VisitExpression(binary.Left, events);
                VisitExpression(binary.Right, events);
                break;
            case IrUnaryExpression unary:
                VisitExpression(unary.Operand, events);
                break;
            case IrConversionExpression conversion:
                VisitExpression(conversion.Operand, events);
                break;
            case IrConditionalExpression conditional:
                VisitExpression(conditional.Condition, events);
                VisitExpression(conditional.WhenTrue, events);
                VisitExpression(conditional.WhenFalse, events);
                break;
            case IrMemberExpression member:
                VisitExpression(member.Receiver, events);
                break;
            case IrElementExpression element:
                VisitExpression(element.Receiver, events);
                foreach (var argument in element.Arguments)
                    VisitExpression(argument, events);
                break;
            case IrInvocationExpression invocation:
                VisitExpression(invocation.Target, events);
                foreach (var argument in invocation.Arguments)
                    VisitExpression(argument, events);
                break;
            case IrObjectCreationExpression creation:
                foreach (var argument in creation.Arguments)
                    VisitExpression(argument, events);
                foreach (var initializer in creation.Initializers)
                    VisitExpression(initializer, events);
                break;
            case IrWithExpression withExpression:
                VisitExpression(withExpression.Receiver, events);
                foreach (var initializer in withExpression.Initializers)
                    VisitExpression(initializer, events);
                break;
            case IrArrayCreationExpression array:
                if (array.Length is not null)
                    VisitExpression(array.Length, events);
                foreach (var item in array.Elements)
                    VisitExpression(item, events);
                break;
            case IrInterpolatedStringExpression interpolated:
                foreach (var interpolation in interpolated.Parts.OfType<IrInterpolation>())
                    VisitExpression(interpolation.Expression, events);
                break;
            case IrAssignmentExpression assignment:
                VisitExpression(assignment.Target, events);
                VisitExpression(assignment.Value, events);
                break;
            case IrLambdaExpression lambda:
                if (lambda.ExpressionBody is not null)
                    VisitExpression(lambda.ExpressionBody, events);
                if (lambda.StatementBody is not null)
                    VisitStatement(lambda.StatementBody, events);
                break;
            case IrQueryExpression query:
                VisitExpression(query.SourceExpression, events);
                foreach (var clause in query.Clauses)
                {
                    switch (clause)
                    {
                        case IrWhereClause where:
                            VisitExpression(where.Predicate, events);
                            break;
                        case IrOrderClause order:
                            VisitExpression(order.Key, events);
                            break;
                        case IrSelectClause select:
                            VisitExpression(select.Projection, events);
                            break;
                        case IrGroupClause group:
                            VisitExpression(group.Element, events);
                            VisitExpression(group.Key, events);
                            break;
                    }
                }
                break;
        }
    }

    private sealed record PlanEvent(IrAwaitExpression? AwaitExpression, IrSymbol? Symbol);
}
