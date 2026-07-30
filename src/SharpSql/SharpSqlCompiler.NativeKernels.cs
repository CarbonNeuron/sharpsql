using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private readonly Dictionary<IrMethodId, NativeKernelPlan?> _nativeKernelPlans = [];
    private readonly List<NativeKernelPlan> _nativeKernelProvisioning = [];

    private void ValidateNativeKernelOptions(IrSource source)
    {
        if (_options.EnableNativeKernels && !UsesMemoryOptimizedRuntime)
        {
            AddDiagnostic(
                "SS8201",
                "Native kernels require TranspileOptions.UseMemoryOptimizedTables.",
                source);
        }
    }

    private bool TryEmitNativeKernelCall(
        MethodDefinition method,
        IReadOnlyList<IrExpression> arguments,
        VariableScope callerScope,
        string? targetSql,
        IrType targetType,
        bool declareTarget)
    {
        if (!_options.EnableNativeKernels || !UsesMemoryOptimizedRuntime || arguments.Count != method.Parameters.Count)
            return false;

        if (!_nativeKernelPlans.TryGetValue(method.Id, out var plan))
        {
            plan = NativeKernelEmitter.TryCreate(method, _methodGraph, _options.ApplicationSchema);
            _nativeKernelPlans.Add(method.Id, plan);
            if (plan is not null)
                _nativeKernelProvisioning.Add(plan);
        }
        if (plan is null)
            return false;

        var result = targetSql ?? _names.Allocate("_native_kernel_discarded");
        if (targetSql is null || declareTarget)
            _sql.Line($"DECLARE {result} {(targetSql is null ? method.ReturnType : targetType).SqlType()};");

        var argumentVariables = new string[arguments.Count];
        for (var index = 0; index < arguments.Count; index++)
        {
            argumentVariables[index] = _names.Allocate($"_native_kernel_argument_{index}");
            _sql.Line(
                $"DECLARE {argumentVariables[index]} {method.Parameters[index].Type.SqlType()} = " +
                $"{EmitScalar(arguments[index], callerScope)};");
        }

        var status = _names.Allocate("_native_kernel_status");
        var lockResult = _names.Allocate("_native_kernel_lock_result");
        var lockResource = NativeKernelRuntimeSqlEmitter.KernelLockResource(
            _options.ApplicationSchema,
            plan.Name);
        _sql.Line($"DECLARE {lockResult} INT;");
        _sql.Line($"EXEC {lockResult} = sys.sp_getapplock");
        using (_sql.Indent())
        {
            _sql.Line($"@Resource = {SqlIdentifier.UnicodeLiteral(lockResource)},");
            _sql.Line("@LockMode = N'Shared',");
            _sql.Line("@LockOwner = N'Session',");
            _sql.Line("@LockTimeout = 60000,");
            _sql.Line("@DbPrincipal = N'public';");
        }
        _sql.Line($"IF {lockResult} < 0 THROW {NativeKernelRuntimeSqlEmitter.LockErrorNumber}, 'Could not acquire the SharpSql native-kernel execution lock.', 1;");
        _sql.Line($"DECLARE {status} INT;");
        _sql.Line("BEGIN TRY");
        using (_sql.Indent())
        {
            _sql.Line($"EXEC {status} = {plan.QualifiedName}");
            using (_sql.Indent())
            {
                for (var index = 0; index < argumentVariables.Length; index++)
                    _sql.Line($"@p{index} = {argumentVariables[index]},");
                _sql.Line($"@__result = {result} OUTPUT;");
            }
            _sql.Line($"EXEC sys.sp_releaseapplock @Resource = {SqlIdentifier.UnicodeLiteral(lockResource)}, @LockOwner = N'Session', @DbPrincipal = N'public';");
        }
        _sql.Line("END TRY");
        _sql.Line("BEGIN CATCH");
        using (_sql.Indent())
        {
            _sql.Line($"EXEC sys.sp_releaseapplock @Resource = {SqlIdentifier.UnicodeLiteral(lockResource)}, @LockOwner = N'Session', @DbPrincipal = N'public';");
            _sql.Line("THROW;");
        }
        _sql.Line("END CATCH;");
        _sql.Line($"IF {status} <> 0 THROW 51930, 'Native SharpSql kernel returned a failure status.', 1;");
        return true;
    }

    private string CompleteSql()
    {
        var program = _sql.ToString();
        if (_nativeKernelProvisioning.Count == 0)
            return program;

        var preamble = new SqlWriter();
        preamble.Line("SET ANSI_NULLS ON;");
        preamble.Line("SET ANSI_PADDING ON;");
        preamble.Line("SET ANSI_WARNINGS ON;");
        preamble.Line("SET ARITHABORT ON;");
        preamble.Line("SET CONCAT_NULL_YIELDS_NULL ON;");
        preamble.Line("SET QUOTED_IDENTIFIER ON;");
        preamble.Line("SET NUMERIC_ROUNDABORT OFF;");
        var schemaName = SqlIdentifier.Validate(_options.ApplicationSchema, nameof(TranspileOptions.ApplicationSchema));
        preamble.Line($"IF SCHEMA_ID({SqlIdentifier.UnicodeLiteral(schemaName)}) IS NULL");
        using (preamble.Indent())
            preamble.Line("THROW 51931, 'Provision the SharpSql memory-optimized runtime before using native kernels.', 1;");
        foreach (var line in NativeKernelRuntimeSqlEmitter.EmitProvisioning(
                     schemaName,
                     _nativeKernelProvisioning.Select(plan => new NativeKernelDefinition(
                         plan.Name,
                         plan.QualifiedName,
                         plan.ProvisioningSql)))
                 .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            preamble.Line(line);
        }
        preamble.Line();
        return preamble + program;
    }

    private sealed record NativeKernelPlan(string Name, string QualifiedName, string ProvisioningSql);

    private static class NativeKernelEmitter
    {
        private const MethodEffects DisallowedEffects =
            MethodEffects.ReadsMutableState |
            MethodEffects.WritesMutableState |
            MethodEffects.Allocates |
            MethodEffects.Nondeterministic |
            MethodEffects.PerformsIo |
            MethodEffects.InvokesUnknown |
            MethodEffects.UsesRandom;

        public static NativeKernelPlan? TryCreate(
            MethodDefinition method,
            MethodGraph? graph,
            string applicationSchema)
        {
            if (method.Id.IsNone || method.IsInstance || method.IsAsync || method.Body is null ||
                method.ReturnType == IrType.Void || !SupportedType(method.ReturnType) ||
                method.Parameters.Any(parameter => !SupportedType(parameter.Type)) ||
                (method.Behavior.Effects & DisallowedEffects) != 0 ||
                graph?.RecursiveMethodIds.Contains(method.Id) == true)
            {
                return null;
            }

            var core = CoreIrLowerer.Lower(method);
            if (core.Method is null || !ContainsLoop(core.Method))
                return null;

            var body = new SqlWriter();
            var symbols = method.Parameters
                .Select((parameter, index) => (parameter.Symbol.Id, Name: $"@p{index}"))
                .ToDictionary(item => item.Id, item => item.Name);
            if (!EmitStatements(body, method.Body.Statements, symbols))
                return null;

            var bodySql = body.ToString();
            var hash = Hash(method.Id.Value + "\n" + bodySql);
            var name = $"NativeKernel_{hash}";
            var qualifiedName = SqlIdentifier.Qualified(applicationSchema, name);
            var procedure = new SqlWriter();
            procedure.Line($"CREATE PROCEDURE {qualifiedName}");
            using (procedure.Indent())
            {
                for (var index = 0; index < method.Parameters.Count; index++)
                    procedure.Line($"@p{index} {method.Parameters[index].Type.SqlType()},");
                procedure.Line($"@__result {method.ReturnType.SqlType()} OUTPUT");
            }
            procedure.Line("WITH NATIVE_COMPILATION, SCHEMABINDING, EXECUTE AS OWNER");
            procedure.Line("AS");
            procedure.Line("BEGIN ATOMIC WITH");
            procedure.Line("(");
            using (procedure.Indent())
            {
                procedure.Line("TRANSACTION ISOLATION LEVEL = SNAPSHOT,");
                procedure.Line("LANGUAGE = N'us_english'");
            }
            procedure.Line(")");
            using (procedure.Indent())
            {
                foreach (var line in bodySql.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    procedure.Line(line);
            }
            procedure.Line("END;");

            var createSql = procedure.ToString().TrimEnd();
            var escapedCreateSql = createSql.Replace("'", "''", StringComparison.Ordinal);
            var provisioning =
                $"IF OBJECT_ID({SqlIdentifier.UnicodeLiteral(qualifiedName)}, N'P') IS NULL EXEC(N'{escapedCreateSql}');";
            return new NativeKernelPlan(name, qualifiedName, provisioning);
        }

        private static bool EmitStatements(
            SqlWriter sql,
            IReadOnlyList<ProceduralStatement> statements,
            Dictionary<IrSymbolId, string> symbols)
        {
            foreach (var statement in statements)
                if (!EmitStatement(sql, statement, symbols))
                    return false;
            return true;
        }

        private static bool EmitStatement(
            SqlWriter sql,
            ProceduralStatement statement,
            Dictionary<IrSymbolId, string> symbols)
        {
            switch (statement)
            {
                case ProceduralBlock block:
                    return EmitStatements(sql, block.Statements, symbols);

                case ProceduralDeclarationStatement declaration:
                    foreach (var variable in declaration.Declaration.Variables)
                    {
                        if (!SupportedType(variable.DeclaredType))
                            return false;
                        var name = $"@v{variable.Symbol.Id.Value}";
                        symbols[variable.Symbol.Id] = name;
                        if (variable.Initializer is null)
                            sql.Line($"DECLARE {name} {variable.DeclaredType.SqlType()};");
                        else if (Render(variable.Initializer, symbols) is { } initializer)
                            sql.Line($"DECLARE {name} {variable.DeclaredType.SqlType()} = {initializer};");
                        else
                            return false;
                    }
                    return true;

                case ProceduralExpressionStatement { Expression: IrAssignmentExpression assignment }:
                    if (assignment.Target is not IrVariableExpression target ||
                        !symbols.TryGetValue(target.Symbol.Id, out var targetName) ||
                        Render(assignment.Value, symbols) is not { } value)
                    {
                        return false;
                    }
                    var assignmentOperator = assignment.Operator switch
                    {
                        IrAssignmentOperator.Assign => string.Empty,
                        IrAssignmentOperator.Add => targetName + " + ",
                        IrAssignmentOperator.Subtract => targetName + " - ",
                        IrAssignmentOperator.Multiply => targetName + " * ",
                        IrAssignmentOperator.Divide => targetName + " / ",
                        IrAssignmentOperator.Remainder => targetName + " % ",
                        IrAssignmentOperator.BitwiseAnd => targetName + " & ",
                        IrAssignmentOperator.BitwiseOr => targetName + " | ",
                        IrAssignmentOperator.ExclusiveOr => targetName + " ^ ",
                        _ => null
                    };
                    if (assignmentOperator is null)
                        return false;
                    sql.Line($"SET {targetName} = {assignmentOperator}{value};");
                    return true;

                case ProceduralExpressionStatement
                {
                    Expression: IrUnaryExpression
                    {
                        Operand: IrVariableExpression variable,
                        Operator: IrUnaryOperator.PreIncrement or IrUnaryOperator.PostIncrement or
                            IrUnaryOperator.PreDecrement or IrUnaryOperator.PostDecrement
                    } unary
                }:
                    if (!symbols.TryGetValue(variable.Symbol.Id, out var variableName))
                        return false;
                    var delta = unary.Operator is IrUnaryOperator.PreIncrement or IrUnaryOperator.PostIncrement
                        ? "+ 1"
                        : "- 1";
                    sql.Line($"SET {variableName} = {variableName} {delta};");
                    return true;

                case ProceduralWhile @while when RenderCondition(@while.Condition, symbols) is { } condition:
                    sql.Line($"WHILE {condition}");
                    sql.Line("BEGIN");
                    using (sql.Indent())
                        if (!EmitStatement(sql, @while.Body, symbols))
                            return false;
                    sql.Line("END;");
                    return true;

                case ProceduralIf @if when RenderCondition(@if.Condition, symbols) is { } condition:
                    sql.Line($"IF {condition}");
                    sql.Line("BEGIN");
                    using (sql.Indent())
                        if (!EmitStatement(sql, @if.Then, symbols))
                            return false;
                    sql.Line("END;");
                    if (@if.Else is not null)
                    {
                        sql.Line("ELSE");
                        sql.Line("BEGIN");
                        using (sql.Indent())
                            if (!EmitStatement(sql, @if.Else, symbols))
                                return false;
                        sql.Line("END;");
                    }
                    return true;

                case ProceduralReturn { Expression: not null } @return when
                    Render(@return.Expression, symbols) is { } result:
                    sql.Line($"SET @__result = {result};");
                    sql.Line("RETURN 0;");
                    return true;

                case ProceduralEmpty:
                    return true;

                default:
                    return false;
            }
        }

        private static string? RenderCondition(
            IrExpression expression,
            IReadOnlyDictionary<IrSymbolId, string> symbols)
        {
            var rendered = Render(expression, symbols);
            return rendered is null ? null : expression.Type.IsBoolean && expression is IrVariableExpression
                ? $"{rendered} = 1"
                : rendered;
        }

        private static string? Render(
            IrExpression expression,
            IReadOnlyDictionary<IrSymbolId, string> symbols) => expression switch
            {
                IrConstantExpression { Value: null } => "NULL",
                IrConstantExpression { Value: bool value } => value ? "CAST(1 AS BIT)" : "CAST(0 AS BIT)",
                IrConstantExpression { Value: IFormattable value } => value.ToString(null, CultureInfo.InvariantCulture),
                IrDefaultValueExpression defaultValue => DefaultSql(defaultValue.Type),
                IrVariableExpression variable when symbols.TryGetValue(variable.Symbol.Id, out var name) => name,
                IrConversionExpression conversion when Render(conversion.Operand, symbols) is { } operand =>
                    $"CONVERT({conversion.TargetType.SqlType()}, {operand})",
                IrUnaryExpression unary when Render(unary.Operand, symbols) is { } operand => unary.Operator switch
                {
                    IrUnaryOperator.Identity => $"(+{operand})",
                    IrUnaryOperator.Negate => $"(-{operand})",
                    IrUnaryOperator.BitwiseNot => $"(~{operand})",
                    IrUnaryOperator.LogicalNot => $"NOT ({operand})",
                    _ => null
                },
                IrBinaryExpression binary when
                    Render(binary.Left, symbols) is { } left &&
                    Render(binary.Right, symbols) is { } right &&
                    BinaryOperator(binary.Operator) is { } operation => $"({left} {operation} {right})",
                _ => null
            };

        private static string? BinaryOperator(IrBinaryOperator operation) => operation switch
        {
            IrBinaryOperator.Add => "+",
            IrBinaryOperator.Subtract => "-",
            IrBinaryOperator.Multiply => "*",
            IrBinaryOperator.Divide => "/",
            IrBinaryOperator.Remainder => "%",
            IrBinaryOperator.BitwiseAnd => "&",
            IrBinaryOperator.BitwiseOr => "|",
            IrBinaryOperator.ExclusiveOr => "^",
            IrBinaryOperator.LogicalAnd => "AND",
            IrBinaryOperator.LogicalOr => "OR",
            IrBinaryOperator.Equal => "=",
            IrBinaryOperator.NotEqual => "<>",
            IrBinaryOperator.LessThan => "<",
            IrBinaryOperator.LessThanOrEqual => "<=",
            IrBinaryOperator.GreaterThan => ">",
            IrBinaryOperator.GreaterThanOrEqual => ">=",
            _ => null
        };

        private static bool SupportedType(IrType type) => type.Name is
            "int" or "long";

        private static bool ContainsLoop(CoreMethod method) => method.Blocks.Any(block =>
            block.Terminator switch
            {
                CoreJump jump => jump.Target.Value <= block.Id.Value,
                CoreBranch branch => branch.WhenTrue.Value <= block.Id.Value ||
                    branch.WhenFalse.Value <= block.Id.Value,
                _ => false
            });

        private static string Hash(string value)
        {
            using var algorithm = SHA256.Create();
            return string.Concat(
                algorithm.ComputeHash(Encoding.UTF8.GetBytes(value))
                    .Take(16)
                    .Select(item => item.ToString("x2", CultureInfo.InvariantCulture)));
        }
    }
}
