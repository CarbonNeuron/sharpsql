using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private const string HeapRandoms = "#__sharpsql_randoms";
    private const string HeapRandomState = "#__sharpsql_random_state";
    private const int RandomMax = int.MaxValue;

    private bool _usesRandom;

    private void EmitRandomTables()
    {
        _sql.Line($"CREATE TABLE {HeapRandoms} (");
        using (_sql.Indent())
        {
            _sql.Line("__object_id BIGINT NOT NULL PRIMARY KEY,");
            _sql.Line("__inext INT NOT NULL,");
            _sql.Line("__inextp INT NOT NULL");
        }
        _sql.Line(");");
        _sql.Line($"CREATE TABLE {HeapRandomState} (");
        using (_sql.Indent())
        {
            _sql.Line("__random_id BIGINT NOT NULL,");
            _sql.Line("__index INT NOT NULL,");
            _sql.Line("__value INT NOT NULL,");
            _sql.Line("PRIMARY KEY (__random_id, __index)");
        }
        _sql.Line(");");
    }

    private bool TryEmitRandomExpression(
        ExpressionSyntax expression,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        if (expression is ObjectCreationExpressionSyntax creation &&
            IsRandomType(NormalizeTypeName(creation.Type.ToString())))
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

    private bool MethodUsesRandom(MethodDefinition method) =>
        method.Syntax.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Any(IsRandomInvocation);

    private void EmitNewRandom(
        ObjectCreationExpressionSyntax creation,
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

        _sql.Line($"DECLARE {random} BIGINT;");
        _sql.Line($"INSERT INTO {HeapObjects} (__type_id) VALUES (1004);");
        _sql.Line($"SET {random} = CONVERT(BIGINT, SCOPE_IDENTITY());");
        _sql.Line($"INSERT INTO {HeapRandoms} (__object_id, __inext, __inextp) VALUES ({random}, 0, 21);");
        _sql.Line($"DECLARE {seedVariable} INT = {seed};");
        _sql.Line($"DECLARE {subtraction} BIGINT = CASE WHEN {seedVariable} = -2147483648 THEN {RandomMax} WHEN {seedVariable} < 0 THEN -CONVERT(BIGINT, {seedVariable}) ELSE {seedVariable} END;");
        _sql.Line($"DECLARE {mj} BIGINT = 161803398 - {subtraction};");
        _sql.Line($"DECLARE {mk} BIGINT = 1;");
        _sql.Line($"DECLARE {index} INT = 0;");
        _sql.Line($"WHILE {index} < 56");
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            _sql.Line($"INSERT INTO {HeapRandomState} (__random_id, __index, __value) VALUES ({random}, {index}, 0);");
            _sql.Line($"SET {index} = {index} + 1;");
        }
        _sql.Line("END;");
        _sql.Line($"UPDATE {HeapRandomState} SET __value = {mj} WHERE __random_id = {random} AND __index = 55;");
        _sql.Line($"DECLARE {shuffleIndex} INT = 0;");
        _sql.Line($"SET {index} = 1;");
        _sql.Line($"WHILE {index} < 55");
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            _sql.Line($"SET {shuffleIndex} = {shuffleIndex} + 21;");
            _sql.Line($"IF {shuffleIndex} >= 55 SET {shuffleIndex} = {shuffleIndex} - 55;");
            _sql.Line($"UPDATE {HeapRandomState} SET __value = {mk} WHERE __random_id = {random} AND __index = {shuffleIndex};");
            _sql.Line($"SET {mk} = {mj} - {mk};");
            EmitInt32Wrap(mk);
            _sql.Line($"IF {mk} < 0 SET {mk} = {mk} + {RandomMax};");
            _sql.Line($"SET {mj} = (SELECT __value FROM {HeapRandomState} WHERE __random_id = {random} AND __index = {shuffleIndex});");
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
                _sql.Line($"SET {stateValue} = CONVERT(BIGINT, (SELECT __value FROM {HeapRandomState} WHERE __random_id = {random} AND __index = {index})) - CONVERT(BIGINT, (SELECT __value FROM {HeapRandomState} WHERE __random_id = {random} AND __index = 1 + {offsetIndex}));");
                EmitInt32Wrap(stateValue);
                _sql.Line($"IF {stateValue} < 0 SET {stateValue} = {stateValue} + {RandomMax};");
                _sql.Line($"UPDATE {HeapRandomState} SET __value = {stateValue} WHERE __random_id = {random} AND __index = {index};");
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

        var captured = new List<VmTemporary>();
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
                var storage = AllocateVmTemporary(CSharpType.Int, context);
                StoreVmTemporary(storage, value);
                captured.Add(storage);
                EvaluateArgument(argumentIndex + 1);
            });
        }

        void EmitCall()
        {
            var random = EmitScalar(member.Expression, scope);
            var firstArgument = captured.Count > 0 ? ReadVmTemporary(captured[0]) : null;
            var secondArgument = captured.Count > 1 ? ReadVmTemporary(captured[1]) : null;

            if (methodName == "Next" && captured.Count == 1)
                _sql.Line($"IF {firstArgument} < 0 THROW 51004, 'Random maximum must be non-negative.', 1;");
            else if (methodName == "Next" && captured.Count == 2)
                _sql.Line($"IF {firstArgument} > {secondArgument} THROW 51005, 'Random minimum must not exceed maximum.', 1;");

            var sample = EmitRandomSample(random);
            if (methodName == "NextDouble")
            {
                var result = _names.Allocate("_random_double");
                _sql.Line($"DECLARE {result} FLOAT = CONVERT(FLOAT, {sample}) * (1.0 / {RandomMax}.0);");
                continuation(result);
                return;
            }

            if (captured.Count == 0)
            {
                continuation(sample);
                return;
            }

            var integerResult = _names.Allocate("_random_result");
            _sql.Line($"DECLARE {integerResult} INT;");
            if (captured.Count == 1)
            {
                _sql.Line($"SET {integerResult} = CONVERT(INT, FLOOR(CONVERT(FLOAT, {sample}) * (1.0 / {RandomMax}.0) * {firstArgument}));");
                continuation(integerResult);
                return;
            }

            var range = _names.Allocate("_random_range");
            _sql.Line($"DECLARE {range} BIGINT = CONVERT(BIGINT, {secondArgument}) - CONVERT(BIGINT, {firstArgument});");
            _sql.Line($"IF {range} <= {RandomMax}");
            _sql.Line($"    SET {integerResult} = CONVERT(INT, FLOOR(CONVERT(FLOAT, {sample}) * (1.0 / {RandomMax}.0) * {range})) + {firstArgument};");
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

    private string EmitRandomSample(string random)
    {
        var next = _names.Allocate("_random_inext");
        var nextPartner = _names.Allocate("_random_inextp");
        var sample = _names.Allocate("_random_sample");
        _sql.Line($"DECLARE {next} INT = (SELECT __inext FROM {HeapRandoms} WHERE __object_id = {random}) + 1;");
        _sql.Line($"IF {next} >= 56 SET {next} = 1;");
        _sql.Line($"DECLARE {nextPartner} INT = (SELECT __inextp FROM {HeapRandoms} WHERE __object_id = {random}) + 1;");
        _sql.Line($"IF {nextPartner} >= 56 SET {nextPartner} = 1;");
        _sql.Line($"DECLARE {sample} BIGINT = CONVERT(BIGINT, (SELECT __value FROM {HeapRandomState} WHERE __random_id = {random} AND __index = {next})) - CONVERT(BIGINT, (SELECT __value FROM {HeapRandomState} WHERE __random_id = {random} AND __index = {nextPartner}));");
        EmitInt32Wrap(sample);
        _sql.Line($"IF {sample} = {RandomMax} SET {sample} = {sample} - 1;");
        _sql.Line($"IF {sample} < 0 SET {sample} = {sample} + {RandomMax};");
        _sql.Line($"UPDATE {HeapRandomState} SET __value = {sample} WHERE __random_id = {random} AND __index = {next};");
        _sql.Line($"UPDATE {HeapRandoms} SET __inext = {next}, __inextp = {nextPartner} WHERE __object_id = {random};");
        return sample;
    }

    private void EmitInt32Wrap(string value)
    {
        _sql.Line($"IF {value} > {RandomMax} SET {value} = {value} - 4294967296;");
        _sql.Line($"IF {value} < -2147483648 SET {value} = {value} + 4294967296;");
    }

    private static bool IsRandomType(string name) => name is "Random" or "System.Random";
}
