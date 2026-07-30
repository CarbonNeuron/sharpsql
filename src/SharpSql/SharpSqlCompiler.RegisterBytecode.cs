using System.Globalization;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private const string BytecodeProgramTable = "#__sharpsql_bc_program";
    private const string BytecodeFramesTable = "#__sharpsql_bc_frames";
    private const string BytecodeRegistersTable = "#__sharpsql_bc_registers";
    private readonly List<VmContinuation> _bytecodeContinuations = [];

    private string BytecodeDispatchLabel => "__sharpsql_bc_dispatch";
    private string BytecodeReturnDispatchLabel => "__sharpsql_bc_return_dispatch";
    private string BytecodeHaltLabel => "__sharpsql_bc_halt";

    private void EmitRegisterBytecodePreamble()
    {
        if (_bytecodeMethods.Count == 0)
            return;

        _sql.Line("-- SharpSql compact register-bytecode runtime ABI 1.0");
        _sql.Line($"DROP TABLE IF EXISTS {BytecodeRegistersTable};");
        _sql.Line($"DROP TABLE IF EXISTS {BytecodeFramesTable};");
        _sql.Line($"DROP TABLE IF EXISTS {BytecodeProgramTable};");
        _sql.Line($"CREATE TABLE {BytecodeProgramTable} (");
        using (_sql.Indent())
        {
            _sql.Line("__method_id INT NOT NULL,");
            _sql.Line("__pc INT NOT NULL,");
            _sql.Line("__opcode TINYINT NOT NULL,");
            _sql.Line("__destination INT NULL,");
            _sql.Line("__type TINYINT NULL,");
            _sql.Line("__operand_a INT NULL,");
            _sql.Line("__operand_b INT NULL,");
            _sql.Line("__operation INT NULL,");
            _sql.Line("__target INT NULL,");
            _sql.Line("__false_target INT NULL,");
            _sql.Line("__constant BIGINT NULL,");
            _sql.Line("PRIMARY KEY (__method_id, __pc)");
        }
        _sql.Line(");");
        _sql.Line($"CREATE TABLE {BytecodeFramesTable} (");
        using (_sql.Indent())
        {
            _sql.Line("__id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,");
            _sql.Line("__method_id INT NOT NULL,");
            _sql.Line("__pc INT NOT NULL,");
            _sql.Line("__return_id INT NOT NULL");
        }
        _sql.Line(");");
        _sql.Line($"CREATE TABLE {BytecodeRegistersTable} (");
        using (_sql.Indent())
        {
            _sql.Line("__frame_id INT NOT NULL,");
            _sql.Line("__register_id INT NOT NULL,");
            _sql.Line("__value BIGINT NULL,");
            _sql.Line("PRIMARY KEY (__frame_id, __register_id)");
        }
        _sql.Line(");");
        EmitRegisterBytecodeImage();
        _sql.Line("DECLARE @__sharpsql_bc_frame_id INT;");
        _sql.Line("DECLARE @__sharpsql_bc_method_id INT;");
        _sql.Line("DECLARE @__sharpsql_bc_pc INT;");
        _sql.Line("DECLARE @__sharpsql_bc_opcode TINYINT;");
        _sql.Line("DECLARE @__sharpsql_bc_destination INT;");
        _sql.Line("DECLARE @__sharpsql_bc_type TINYINT;");
        _sql.Line("DECLARE @__sharpsql_bc_operand_a INT;");
        _sql.Line("DECLARE @__sharpsql_bc_operand_b INT;");
        _sql.Line("DECLARE @__sharpsql_bc_operation INT;");
        _sql.Line("DECLARE @__sharpsql_bc_target INT;");
        _sql.Line("DECLARE @__sharpsql_bc_false_target INT;");
        _sql.Line("DECLARE @__sharpsql_bc_constant BIGINT;");
        _sql.Line("DECLARE @__sharpsql_bc_a BIGINT;");
        _sql.Line("DECLARE @__sharpsql_bc_b BIGINT;");
        _sql.Line("DECLARE @__sharpsql_bc_value BIGINT;");
        _sql.Line("DECLARE @__sharpsql_bc_result BIGINT;");
        _sql.Line("DECLARE @__sharpsql_bc_jump INT;");
        _sql.Line();
    }

    private void EmitRegisterBytecodeImage()
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var method in _bytecodeMethods.Values.OrderBy(method => method.Id.Value))
        {
            var types = method.Registers.ToDictionary(register => register.Register, register => register.Type);
            for (var pc = 0; pc < method.Instructions.Count; pc++)
            {
                var (columns, row) = CompactRegisterBytecodeRow(
                    method.Id, pc, method.Instructions[pc], types);
                if (!groups.TryGetValue(columns, out var rows))
                {
                    rows = [];
                    groups.Add(columns, rows);
                }
                rows.Add(row);
            }
        }
        foreach (var (columns, rows) in groups)
        {
            for (var offset = 0; offset < rows.Count; offset += 1000)
            {
                var count = Math.Min(1000, rows.Count - offset);
                _sql.Line($"INSERT INTO {BytecodeProgramTable} ({columns}) VALUES");
                using (_sql.Indent())
                {
                    for (var index = 0; index < count; index++)
                        _sql.Line(rows[offset + index] + (index + 1 == count ? ";" : ","));
                }
            }
        }
    }

    private static (string Columns, string Row) CompactRegisterBytecodeRow(
        BytecodeMethodId method,
        int pc,
        RegisterBytecodeInstruction instruction,
        IReadOnlyDictionary<BytecodeRegister, IrType> types)
    {
        var prefix = $"{method.Value}, {pc}, {(int)instruction.OpCode}";
        return instruction switch
        {
            BytecodeConstantInstruction item =>
                ("__method_id, __pc, __opcode, __destination, __type, __constant",
                    $"({prefix}, {item.Destination.Value}, {BytecodeType(item.Type)}, {Sql(ConstantInt64(item.Value))})"),
            BytecodeMoveInstruction item =>
                ("__method_id, __pc, __opcode, __destination, __type, __operand_a",
                    $"({prefix}, {item.Destination.Value}, {BytecodeType(types[item.Destination])}, {item.Source.Value})"),
            BytecodeConvertInstruction item =>
                ("__method_id, __pc, __opcode, __destination, __type, __operand_a",
                    $"({prefix}, {item.Destination.Value}, {BytecodeType(item.Type)}, {item.Source.Value})"),
            BytecodeUnaryInstruction item =>
                ("__method_id, __pc, __opcode, __destination, __type, __operand_a, __operation",
                    $"({prefix}, {item.Destination.Value}, {BytecodeType(item.Type)}, {item.Operand.Value}, {(int)item.Operator})"),
            BytecodeBinaryInstruction item =>
                ("__method_id, __pc, __opcode, __destination, __type, __operand_a, __operand_b, __operation",
                    $"({prefix}, {item.Destination.Value}, {BytecodeType(item.Type)}, {item.Left.Value}, {item.Right.Value}, {(int)item.Operator})"),
            BytecodeBranchInstruction item =>
                ("__method_id, __pc, __opcode, __operand_a, __target, __false_target",
                    $"({prefix}, {Sql(item.Condition?.Value)}, {item.WhenTrue.Value}, {Sql(item.WhenFalse?.Value)})"),
            BytecodeCallInstruction item =>
                ("__method_id, __pc, __opcode, __destination, __target",
                    $"({prefix}, {Sql(item.Destination?.Value)}, {item.Target.Value})"),
            BytecodeReturnInstruction item =>
                ("__method_id, __pc, __opcode, __operand_a",
                    $"({prefix}, {Sql(item.Value?.Value)})"),
            _ => throw new InvalidOperationException($"Unknown register bytecode instruction '{instruction.GetType().Name}'.")
        };
    }

    private static string Sql<T>(T? value) where T : struct =>
        value is null ? "NULL" : Convert.ToString(value.Value, CultureInfo.InvariantCulture)!;

    private static int BytecodeType(IrType type) => type.Name switch
    {
        "bool" => 1,
        "int" => 2,
        "long" => 3,
        _ => throw new InvalidOperationException($"Unsupported register-bytecode type '{type.Name}'.")
    };

    private static long? ConstantInt64(object? value) => value switch
    {
        null => null,
        bool boolean => boolean ? 1L : 0L,
        int integer => integer,
        long integer => integer,
        _ => throw new InvalidOperationException($"Unsupported register-bytecode constant '{value}'.")
    };

    private void EmitRegisterBytecodeEpilogue()
    {
        if (_bytecodeMethods.Count == 0)
            return;

        _sql.Line($"GOTO {BytecodeHaltLabel};");
        _sql.Line();
        EmitLabel(BytecodeDispatchLabel);
        _sql.Line("SELECT @__sharpsql_bc_method_id = __method_id, @__sharpsql_bc_pc = __pc FROM #__sharpsql_bc_frames WHERE __id = @__sharpsql_bc_frame_id;");
        _sql.Line("SET @__sharpsql_bc_opcode = NULL;");
        _sql.Line("SELECT @__sharpsql_bc_opcode = __opcode, @__sharpsql_bc_destination = __destination, @__sharpsql_bc_type = __type, @__sharpsql_bc_operand_a = __operand_a, @__sharpsql_bc_operand_b = __operand_b, @__sharpsql_bc_operation = __operation, @__sharpsql_bc_target = __target, @__sharpsql_bc_false_target = __false_target, @__sharpsql_bc_constant = __constant FROM #__sharpsql_bc_program WHERE __method_id = @__sharpsql_bc_method_id AND __pc = @__sharpsql_bc_pc;");
        _sql.Line("IF @__sharpsql_bc_opcode IS NULL THROW 51031, 'Register bytecode program counter is invalid.', 1;");

        EmitBytecodeValueHandler((int)RegisterBytecodeOpCode.Constant, "@__sharpsql_bc_constant");
        EmitBytecodeMoveOrConvertHandler(RegisterBytecodeOpCode.Move);
        EmitBytecodeMoveOrConvertHandler(RegisterBytecodeOpCode.Convert);
        EmitBytecodeUnaryHandler();
        EmitBytecodeBinaryHandler();

        _sql.Line($"IF @__sharpsql_bc_opcode = {(int)RegisterBytecodeOpCode.Branch}");
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            _sql.Line("IF @__sharpsql_bc_operand_a IS NULL SET @__sharpsql_bc_pc = @__sharpsql_bc_target;");
            _sql.Line("ELSE");
            _sql.Line("BEGIN");
            using (_sql.Indent())
            {
                EmitBytecodeRead("@__sharpsql_bc_operand_a", "@__sharpsql_bc_a");
                _sql.Line("SET @__sharpsql_bc_pc = CASE WHEN @__sharpsql_bc_a <> 0 THEN @__sharpsql_bc_target ELSE @__sharpsql_bc_false_target END;");
            }
            _sql.Line("END;");
            _sql.Line("UPDATE #__sharpsql_bc_frames SET __pc = @__sharpsql_bc_pc WHERE __id = @__sharpsql_bc_frame_id;");
            _sql.Line($"GOTO {BytecodeDispatchLabel};");
        }
        _sql.Line("END;");

        _sql.Line($"IF @__sharpsql_bc_opcode = {(int)RegisterBytecodeOpCode.Call} THROW 51032, 'Nested register-bytecode calls are not enabled by ABI 1.0.', 1;");
        _sql.Line($"IF @__sharpsql_bc_opcode = {(int)RegisterBytecodeOpCode.Return}");
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            _sql.Line("SET @__sharpsql_bc_result = NULL;");
            _sql.Line("IF @__sharpsql_bc_operand_a IS NOT NULL");
            using (_sql.Indent())
                EmitBytecodeRead("@__sharpsql_bc_operand_a", "@__sharpsql_bc_result");
            _sql.Line("SELECT @__sharpsql_bc_jump = __return_id FROM #__sharpsql_bc_frames WHERE __id = @__sharpsql_bc_frame_id;");
            _sql.Line("DELETE FROM #__sharpsql_bc_registers WHERE __frame_id = @__sharpsql_bc_frame_id;");
            _sql.Line("DELETE FROM #__sharpsql_bc_frames WHERE __id = @__sharpsql_bc_frame_id;");
            _sql.Line($"GOTO {BytecodeReturnDispatchLabel};");
        }
        _sql.Line("END;");
        _sql.Line("THROW 51033, 'Register bytecode opcode is not supported by this runtime ABI.', 1;");

        EmitLabel(BytecodeReturnDispatchLabel);
        foreach (var continuation in _bytecodeContinuations)
            _sql.Line($"IF @__sharpsql_bc_jump = {continuation.Id} GOTO {continuation.Label};");
        _sql.Line("THROW 51034, 'Register bytecode continuation was not found.', 1;");
        EmitLabel(BytecodeHaltLabel);
        _sql.Line($"DROP TABLE IF EXISTS {BytecodeRegistersTable};");
        _sql.Line($"DROP TABLE IF EXISTS {BytecodeFramesTable};");
        _sql.Line($"DROP TABLE IF EXISTS {BytecodeProgramTable};");
    }

    private void EmitBytecodeValueHandler(int opcode, string value)
    {
        _sql.Line($"IF @__sharpsql_bc_opcode = {opcode}");
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            _sql.Line($"SET @__sharpsql_bc_value = {value};");
            EmitBytecodeStore();
            EmitBytecodeAdvance();
        }
        _sql.Line("END;");
    }

    private void EmitBytecodeMoveOrConvertHandler(RegisterBytecodeOpCode opcode)
    {
        _sql.Line($"IF @__sharpsql_bc_opcode = {(int)opcode}");
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            EmitBytecodeRead("@__sharpsql_bc_operand_a", "@__sharpsql_bc_value");
            EmitBytecodeStore();
            EmitBytecodeAdvance();
        }
        _sql.Line("END;");
    }

    private void EmitBytecodeUnaryHandler()
    {
        _sql.Line($"IF @__sharpsql_bc_opcode = {(int)RegisterBytecodeOpCode.Unary}");
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            EmitBytecodeRead("@__sharpsql_bc_operand_a", "@__sharpsql_bc_a");
            _sql.Line($"SET @__sharpsql_bc_value = CASE @__sharpsql_bc_operation WHEN {(int)IrUnaryOperator.Identity} THEN @__sharpsql_bc_a WHEN {(int)IrUnaryOperator.Negate} THEN -@__sharpsql_bc_a WHEN {(int)IrUnaryOperator.LogicalNot} THEN CASE WHEN @__sharpsql_bc_a = 0 THEN 1 ELSE 0 END WHEN {(int)IrUnaryOperator.BitwiseNot} THEN ~@__sharpsql_bc_a END;");
            EmitBytecodeStore();
            EmitBytecodeAdvance();
        }
        _sql.Line("END;");
    }

    private void EmitBytecodeBinaryHandler()
    {
        _sql.Line($"IF @__sharpsql_bc_opcode = {(int)RegisterBytecodeOpCode.Binary}");
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            EmitBytecodeRead("@__sharpsql_bc_operand_a", "@__sharpsql_bc_a");
            EmitBytecodeRead("@__sharpsql_bc_operand_b", "@__sharpsql_bc_b");
            _sql.Line("SET @__sharpsql_bc_value = CASE @__sharpsql_bc_operation");
            using (_sql.Indent())
            {
                _sql.Line($"WHEN {(int)IrBinaryOperator.Add} THEN @__sharpsql_bc_a + @__sharpsql_bc_b");
                _sql.Line($"WHEN {(int)IrBinaryOperator.Subtract} THEN @__sharpsql_bc_a - @__sharpsql_bc_b");
                _sql.Line($"WHEN {(int)IrBinaryOperator.Multiply} THEN @__sharpsql_bc_a * @__sharpsql_bc_b");
                _sql.Line($"WHEN {(int)IrBinaryOperator.Divide} THEN @__sharpsql_bc_a / @__sharpsql_bc_b");
                _sql.Line($"WHEN {(int)IrBinaryOperator.Remainder} THEN @__sharpsql_bc_a % @__sharpsql_bc_b");
                _sql.Line($"WHEN {(int)IrBinaryOperator.BitwiseAnd} THEN @__sharpsql_bc_a & @__sharpsql_bc_b");
                _sql.Line($"WHEN {(int)IrBinaryOperator.BitwiseOr} THEN @__sharpsql_bc_a | @__sharpsql_bc_b");
                _sql.Line($"WHEN {(int)IrBinaryOperator.ExclusiveOr} THEN @__sharpsql_bc_a ^ @__sharpsql_bc_b");
                _sql.Line($"WHEN {(int)IrBinaryOperator.LeftShift} THEN @__sharpsql_bc_a << (@__sharpsql_bc_b & CASE WHEN @__sharpsql_bc_type = 3 THEN 63 ELSE 31 END)");
                _sql.Line($"WHEN {(int)IrBinaryOperator.RightShift} THEN CONVERT(BIGINT, FLOOR(CONVERT(DECIMAL(38,0), @__sharpsql_bc_a) / POWER(CONVERT(DECIMAL(38,0), 2), (@__sharpsql_bc_b & CASE WHEN @__sharpsql_bc_type = 3 THEN 63 ELSE 31 END))))");
                _sql.Line($"WHEN {(int)IrBinaryOperator.Equal} THEN CASE WHEN @__sharpsql_bc_a = @__sharpsql_bc_b THEN 1 ELSE 0 END");
                _sql.Line($"WHEN {(int)IrBinaryOperator.NotEqual} THEN CASE WHEN @__sharpsql_bc_a <> @__sharpsql_bc_b THEN 1 ELSE 0 END");
                _sql.Line($"WHEN {(int)IrBinaryOperator.LessThan} THEN CASE WHEN @__sharpsql_bc_a < @__sharpsql_bc_b THEN 1 ELSE 0 END");
                _sql.Line($"WHEN {(int)IrBinaryOperator.LessThanOrEqual} THEN CASE WHEN @__sharpsql_bc_a <= @__sharpsql_bc_b THEN 1 ELSE 0 END");
                _sql.Line($"WHEN {(int)IrBinaryOperator.GreaterThan} THEN CASE WHEN @__sharpsql_bc_a > @__sharpsql_bc_b THEN 1 ELSE 0 END");
                _sql.Line($"WHEN {(int)IrBinaryOperator.GreaterThanOrEqual} THEN CASE WHEN @__sharpsql_bc_a >= @__sharpsql_bc_b THEN 1 ELSE 0 END");
            }
            _sql.Line("END;");
            EmitBytecodeStore();
            EmitBytecodeAdvance();
        }
        _sql.Line("END;");
    }

    private void EmitBytecodeRead(string register, string destination)
    {
        _sql.Line($"SET {destination} = NULL;");
        _sql.Line($"SELECT {destination} = __value FROM {BytecodeRegistersTable} WHERE __frame_id = @__sharpsql_bc_frame_id AND __register_id = {register};");
    }

    private void EmitBytecodeStore()
    {
        _sql.Line("IF @__sharpsql_bc_type = 1 SET @__sharpsql_bc_value = CASE WHEN @__sharpsql_bc_value = 0 THEN 0 ELSE 1 END;");
        _sql.Line("IF @__sharpsql_bc_type = 2 SET @__sharpsql_bc_value = CONVERT(BIGINT, CONVERT(INT, @__sharpsql_bc_value));");
        _sql.Line($"UPDATE {BytecodeRegistersTable} SET __value = @__sharpsql_bc_value WHERE __frame_id = @__sharpsql_bc_frame_id AND __register_id = @__sharpsql_bc_destination;");
        _sql.Line($"IF @@ROWCOUNT = 0 INSERT INTO {BytecodeRegistersTable} (__frame_id, __register_id, __value) VALUES (@__sharpsql_bc_frame_id, @__sharpsql_bc_destination, @__sharpsql_bc_value);");
    }

    private void EmitBytecodeAdvance()
    {
        _sql.Line("UPDATE #__sharpsql_bc_frames SET __pc = __pc + 1 WHERE __id = @__sharpsql_bc_frame_id;");
        _sql.Line($"GOTO {BytecodeDispatchLabel};");
    }

    private void EmitRegisterBytecodeInvocation(
        IrInvocationExpression invocation,
        RegisterBytecodeMethod callee,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        var definition = _methods.Values.Single(method => method.Id == callee.SourceMethod);
        var arguments = InvocationArgumentExpressions(invocation, definition);
        if (arguments.Count != callee.Parameters.Count)
        {
            AddDiagnostic("SS8002", $"Register-bytecode method '{callee.Name}' expects {callee.Parameters.Count} arguments.", invocation.Source);
            continuation("NULL");
            return;
        }

        var captured = new List<VmTemporary>();
        Evaluate(0);
        void Evaluate(int index)
        {
            if (index == arguments.Count)
            {
                EmitCall();
                return;
            }
            EmitVmExpression(arguments[index], scope, context, value =>
            {
                var temporary = AllocateVmTemporary(arguments[index].Type, context);
                StoreVmTemporary(temporary, value);
                captured.Add(temporary);
                Evaluate(index + 1);
            });
        }

        void EmitCall()
        {
            if (context is not null)
                SaveVmRegisters(context);
            var returnLabel = _names.AllocateLabel($"bc_return_{callee.Name}");
            var returnId = ++_nextVmContinuationId;
            _bytecodeContinuations.Add(new VmContinuation(returnId, returnLabel));
            _sql.Line($"INSERT INTO {BytecodeFramesTable} (__method_id, __pc, __return_id) VALUES ({callee.Id.Value}, 0, {returnId});");
            _sql.Line("SET @__sharpsql_bc_frame_id = CONVERT(INT, SCOPE_IDENTITY());");
            for (var index = 0; index < captured.Count; index++)
            {
                _sql.Line($"INSERT INTO {BytecodeRegistersTable} (__frame_id, __register_id, __value) VALUES (@__sharpsql_bc_frame_id, {callee.Parameters[index].Register.Value}, CONVERT(BIGINT, {ReadVmTemporary(captured[index])}));");
            }
            _sql.Line($"GOTO {BytecodeDispatchLabel};");
            EmitLabel(returnLabel);
            if (context is not null)
                LoadVmRegisters(context);
            continuation(callee.ReturnType == IrType.Void
                ? "NULL"
                : $"CONVERT({callee.ReturnType.SqlType()}, @__sharpsql_bc_result)");
        }
    }

    private bool TryGetRegisterBytecodeMethod(
        IrInvocationExpression invocation,
        out RegisterBytecodeMethod method)
    {
        if (TryGetMethod(invocation, out var definition) &&
            _bytecodeMethods.TryGetValue(definition.Id, out method!))
            return true;
        method = null!;
        return false;
    }
}
