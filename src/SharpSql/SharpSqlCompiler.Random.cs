using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private const int RandomMax = int.MaxValue;

    private bool _usesRandom;

    private bool TryEmitRandomExpression(
        ExpressionSyntax expression,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        if (expression is BaseObjectCreationExpressionSyntax creation &&
            IsRandomType(CreationTypeName(creation)))
        {
            EmitNewRandom(creation, scope, context, continuation);
            return true;
        }

        if (expression is InvocationExpressionSyntax invocation && IsRandomInvocation(invocation))
        {
            EmitRandomInvocation(invocation, scope, context, continuation);
            return true;
        }

        return false;
    }

    private bool IsRandomInvocation(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax member &&
        IsRandomType(InferType(member.Expression, new VariableScope()).Name) &&
        member.Name.Identifier.ValueText is "Next" or "NextDouble";

    private static bool MethodUsesRandom(MethodDefinition method) =>
        method.Behavior.Effects.HasFlag(MethodEffects.UsesRandom);

    private void EmitNewRandom(
        BaseObjectCreationExpressionSyntax creation,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        var arguments = creation.ArgumentList?.Arguments ?? default;
        if (arguments.Count > 1)
        {
            AddDiagnostic("SS6401", "Random expects zero arguments or one integer seed.", creation);
            continuation("NULL");
            return;
        }

        if (arguments.Count == 0)
        {
            EmitRandomInitialization("CHECKSUM(NEWID())", continuation);
            return;
        }

        EmitVmExpression(arguments[0].Expression, scope, context, seed =>
            EmitRandomInitialization(seed, continuation));
    }

    private void EmitRandomInitialization(string seed, Action<string> continuation)
    {
        var random = _names.Allocate("_random");
        var seedVariable = _names.Allocate("_random_seed");
        var subtraction = _names.Allocate("_random_subtraction");
        var mj = _names.Allocate("_random_mj");
        var mk = _names.Allocate("_random_mk");
        var index = _names.Allocate("_random_index");
        var shuffleIndex = _names.Allocate("_random_shuffle_index");
        var pass = _names.Allocate("_random_pass");
        var offsetIndex = _names.Allocate("_random_offset_index");
        var stateValue = _names.Allocate("_random_state_value");

        _sql.Line($"DECLARE {random} INT;");
        _sql.Line($"INSERT INTO {HeapObjects} (__type_id, __random_inext, __random_inextp) VALUES (1004, 0, 21);");
        _sql.Line($"SET {random} = CONVERT(INT, SCOPE_IDENTITY());");
        _sql.Line($"DECLARE {seedVariable} INT = {seed};");
        _sql.Line($"DECLARE {subtraction} BIGINT = CASE WHEN {seedVariable} = -2147483648 THEN {RandomMax} WHEN {seedVariable} < 0 THEN -CONVERT(BIGINT, {seedVariable}) ELSE {seedVariable} END;");
        _sql.Line($"DECLARE {mj} BIGINT = 161803398 - {subtraction};");
        _sql.Line($"DECLARE {mk} BIGINT = 1;");
        _sql.Line($"DECLARE {index} INT = 0;");
        _sql.Line($"WHILE {index} < 56");
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            _sql.Line($"INSERT INTO {HeapIndexedItems} (__owner_id, __index, __value) VALUES ({random}, {index}, CONVERT(SQL_VARIANT, 0));");
            _sql.Line($"SET {index} = {index} + 1;");
        }
        _sql.Line("END;");
        _sql.Line($"UPDATE {HeapIndexedItems} SET __value = CONVERT(SQL_VARIANT, CONVERT(INT, {mj})) WHERE __owner_id = {random} AND __index = 55;");
        _sql.Line($"DECLARE {shuffleIndex} INT = 0;");
        _sql.Line($"SET {index} = 1;");
        _sql.Line($"WHILE {index} < 55");
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            _sql.Line($"SET {shuffleIndex} = {shuffleIndex} + 21;");
            _sql.Line($"IF {shuffleIndex} >= 55 SET {shuffleIndex} = {shuffleIndex} - 55;");
            _sql.Line($"UPDATE {HeapIndexedItems} SET __value = CONVERT(SQL_VARIANT, CONVERT(INT, {mk})) WHERE __owner_id = {random} AND __index = {shuffleIndex};");
            _sql.Line($"SET {mk} = {mj} - {mk};");
            EmitInt32Wrap(mk);
            _sql.Line($"IF {mk} < 0 SET {mk} = {mk} + {RandomMax};");
            _sql.Line($"SET {mj} = CONVERT(INT, (SELECT __value FROM {HeapIndexedItems} WHERE __owner_id = {random} AND __index = {shuffleIndex}));");
            _sql.Line($"SET {index} = {index} + 1;");
        }
        _sql.Line("END;");
        _sql.Line($"DECLARE {pass} INT = 1;");
        _sql.Line($"DECLARE {offsetIndex} INT;");
        _sql.Line($"DECLARE {stateValue} BIGINT;");
        _sql.Line($"WHILE {pass} < 5");
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            _sql.Line($"SET {index} = 1;");
            _sql.Line($"WHILE {index} < 56");
            _sql.Line("BEGIN");
            using (_sql.Indent())
            {
                _sql.Line($"SET {offsetIndex} = {index} + 30;");
                _sql.Line($"IF {offsetIndex} >= 55 SET {offsetIndex} = {offsetIndex} - 55;");
                _sql.Line($"SET {stateValue} = CONVERT(BIGINT, (SELECT __value FROM {HeapIndexedItems} WHERE __owner_id = {random} AND __index = {index})) - CONVERT(BIGINT, (SELECT __value FROM {HeapIndexedItems} WHERE __owner_id = {random} AND __index = 1 + {offsetIndex}));");
                EmitInt32Wrap(stateValue);
                _sql.Line($"IF {stateValue} < 0 SET {stateValue} = {stateValue} + {RandomMax};");
                _sql.Line($"UPDATE {HeapIndexedItems} SET __value = CONVERT(SQL_VARIANT, CONVERT(INT, {stateValue})) WHERE __owner_id = {random} AND __index = {index};");
                _sql.Line($"SET {index} = {index} + 1;");
            }
            _sql.Line("END;");
            _sql.Line($"SET {pass} = {pass} + 1;");
        }
        _sql.Line("END;");
        continuation(random);
    }

    private void EmitRandomInvocation(
        InvocationExpressionSyntax invocation,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        var member = (MemberAccessExpressionSyntax)invocation.Expression;
        var methodName = member.Name.Identifier.ValueText;
        var arguments = invocation.ArgumentList.Arguments;
        if ((methodName == "NextDouble" && arguments.Count != 0) ||
            (methodName == "Next" && arguments.Count > 2))
        {
            AddDiagnostic("SS6402", $"Unsupported Random.{methodName} overload.", invocation);
            continuation("NULL");
            return;
        }

        var evaluatedArguments = new List<string>();
        EvaluateArgument(0);

        void EvaluateArgument(int argumentIndex)
        {
            if (argumentIndex == arguments.Count)
            {
                EmitCall();
                return;
            }

            EmitVmExpression(arguments[argumentIndex].Expression, scope, context, value =>
            {
                var mustCapture = ContainsRuntimeExpression(arguments[argumentIndex].Expression) ||
                                  arguments.Skip(argumentIndex + 1)
                                      .Any(argument => ContainsRuntimeExpression(argument.Expression));
                if (mustCapture)
                {
                    var storage = AllocateVmTemporary(IrType.Int, context);
                    StoreVmTemporary(storage, value);
                    evaluatedArguments.Add(ReadVmTemporary(storage));
                }
                else
                {
                    evaluatedArguments.Add(value);
                }
                EvaluateArgument(argumentIndex + 1);
            });
        }

        void EmitCall()
        {
            var random = EmitScalar(member.Expression, scope);
            var firstArgument = evaluatedArguments.Count > 0 ? evaluatedArguments[0] : null;
            var secondArgument = evaluatedArguments.Count > 1 ? evaluatedArguments[1] : null;

            if (methodName == "Next" && evaluatedArguments.Count == 1)
                _sql.Line($"IF {firstArgument} < 0 THROW 51004, 'Random maximum must be non-negative.', 1;");
            else if (methodName == "Next" && evaluatedArguments.Count == 2)
                _sql.Line($"IF {firstArgument} > {secondArgument} THROW 51005, 'Random minimum must not exceed maximum.', 1;");

            var sample = EmitRandomSample(random);
            if (methodName == "NextDouble")
            {
                var result = _names.Allocate("_random_double");
                _sql.Line($"DECLARE {result} FLOAT = CONVERT(FLOAT, {sample}) * (CAST(1 AS FLOAT) / CAST({RandomMax} AS FLOAT));");
                continuation(result);
                return;
            }

            if (evaluatedArguments.Count == 0)
            {
                continuation(sample);
                return;
            }

            var integerResult = _names.Allocate("_random_result");
            if (evaluatedArguments.Count == 1)
            {
                _sql.Line($"DECLARE {integerResult} INT = CONVERT(INT, CONVERT(FLOAT, {sample}) * (CAST(1 AS FLOAT) / CAST({RandomMax} AS FLOAT)) * {firstArgument});");
                continuation(integerResult);
                return;
            }

            var range = _names.Allocate("_random_range");
            _sql.Line($"DECLARE {range} BIGINT = CONVERT(BIGINT, {secondArgument}) - CONVERT(BIGINT, {firstArgument});");
            if (RandomRangeFitsInt32(arguments, scope))
            {
                _sql.Line($"DECLARE {integerResult} INT = CONVERT(INT, CONVERT(FLOAT, {sample}) * (CAST(1 AS FLOAT) / CAST({RandomMax} AS FLOAT)) * {range}) + {firstArgument};");
                continuation(integerResult);
                return;
            }

            _sql.Line($"DECLARE {integerResult} INT;");
            _sql.Line($"IF {range} <= {RandomMax}");
            _sql.Line($"    SET {integerResult} = CONVERT(INT, CONVERT(FLOAT, {sample}) * (CAST(1 AS FLOAT) / CAST({RandomMax} AS FLOAT)) * {range}) + {firstArgument};");
            _sql.Line("ELSE");
            _sql.Line("BEGIN");
            using (_sql.Indent())
            {
                var signSample = EmitRandomSample(random);
                var largeSample = _names.Allocate("_random_large_sample");
                _sql.Line($"DECLARE {largeSample} FLOAT = CONVERT(FLOAT, {sample});");
                _sql.Line($"IF {signSample} % 2 = 0 SET {largeSample} = -{largeSample};");
                _sql.Line($"SET {largeSample} = ({largeSample} + {RandomMax - 1}.0) / 4294967293.0;");
                _sql.Line($"SET {integerResult} = CONVERT(INT, CONVERT(BIGINT, FLOOR({largeSample} * {range})) + {firstArgument});");
            }
            _sql.Line("END;");
            continuation(integerResult);
        }
    }

    private bool RandomRangeFitsInt32(SeparatedSyntaxList<ArgumentSyntax> arguments, VariableScope scope)
    {
        if (arguments.Count != 2)
            return false;
        var hasMinimum = TryGetInt32Constant(arguments[0].Expression, scope, out var minimum);
        var hasMaximum = TryGetInt32Constant(arguments[1].Expression, scope, out var maximum);
        if (hasMinimum && hasMaximum)
            return (long)maximum - minimum <= RandomMax;
        return hasMinimum && minimum >= 0 || hasMaximum && maximum < 0;
    }

    private bool TryGetInt32Constant(ExpressionSyntax expression, VariableScope scope, out int value)
    {
        var facts = AnalyzeExpression(expression, scope);
        if (facts.HasConstantValue && facts.ConstantValue is not null)
        {
            try
            {
                value = Convert.ToInt32(facts.ConstantValue, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
            {
                // The argument is not an Int32 constant.
            }
        }
        value = default;
        return false;
    }

    private string EmitRandomSample(string random)
    {
        var next = _names.Allocate("_random_inext");
        var nextPartner = _names.Allocate("_random_inextp");
        var sample = _names.Allocate("_random_sample");
        _sql.Line($"DECLARE {next} INT = (SELECT __random_inext FROM {HeapObjects} WHERE __id = {random}) % 55 + 1;");
        _sql.Line($"DECLARE {nextPartner} INT = (SELECT __random_inextp FROM {HeapObjects} WHERE __id = {random}) % 55 + 1;");
        _sql.Line($"DECLARE {sample} BIGINT = CONVERT(BIGINT, (SELECT __value FROM {HeapIndexedItems} WHERE __owner_id = {random} AND __index = {next})) - CONVERT(BIGINT, (SELECT __value FROM {HeapIndexedItems} WHERE __owner_id = {random} AND __index = {nextPartner}));");
        _sql.Line($"SET {sample} = (({sample} + 2147483648) % 4294967296 + 4294967296) % 4294967296 - 2147483648;");
        _sql.Line($"SET {sample} = CASE WHEN {sample} = {RandomMax} THEN {sample} - 1 WHEN {sample} < 0 THEN {sample} + {RandomMax} ELSE {sample} END;");
        _sql.Line($"UPDATE {HeapIndexedItems} SET __value = CONVERT(SQL_VARIANT, CONVERT(INT, {sample})) WHERE __owner_id = {random} AND __index = {next};");
        _sql.Line($"UPDATE {HeapObjects} SET __random_inext = {next}, __random_inextp = {nextPartner} WHERE __id = {random};");
        return sample;
    }

    private void EmitInt32Wrap(string value)
    {
        _sql.Line($"IF {value} > {RandomMax} SET {value} = {value} - 4294967296;");
        _sql.Line($"IF {value} < -2147483648 SET {value} = {value} + 4294967296;");
    }

    private static bool IsRandomType(string name) => name is "Random" or "System.Random";
}
