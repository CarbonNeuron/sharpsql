using System.Globalization;
using System.Text;

namespace SharpSql;

internal sealed record RegisterBytecodeLoweringResult(
    RegisterBytecodeMethod? Method,
    string? UnsupportedReason)
{
    public bool Success => Method is not null;
}

internal static class RegisterBytecodeLowerer
{
    public static RegisterBytecodeLoweringResult Lower(
        MethodDefinition source,
        CoreMethod core,
        BytecodeMethodId methodId,
        IReadOnlyDictionary<IrMethodId, BytecodeMethodId>? methodIds = null)
    {
        var registerTypes = new Dictionary<CoreValueId, IrType>();
        foreach (var parameter in core.Parameters)
            registerTypes[parameter.Value] = parameter.Type;
        foreach (var local in core.Locals)
            registerTypes[local.Value] = local.Type;
        foreach (var instruction in core.Blocks.SelectMany(block => block.Instructions))
        {
            if (instruction is CoreHostCallInstruction ||
                instruction is CoreCallInstruction call && call.Type == IrType.Void)
                continue;
            registerTypes[instruction.Result] = instruction.Type;
        }

        var unsupported = registerTypes.Values.FirstOrDefault(type => !RegisterBytecodeContract.IsRuntimeType(type));
        if (unsupported is not null)
            return new(null, $"Runtime type '{unsupported.Name}' is not supported by register bytecode yet.");
        foreach (var instruction in core.Blocks.SelectMany(block => block.Instructions))
        {
            switch (instruction)
            {
                case CoreConvertInstruction convert
                    when (convert.Type.IsString || registerTypes[convert.Operand].IsString) &&
                         convert.Type != registerTypes[convert.Operand]:
                    return new(null, "Register bytecode supports only identity conversions involving strings.");
                case CoreUnaryInstruction unary when registerTypes[unary.Operand].IsString:
                    return new(null, $"Unary operator '{unary.Operator}' is not supported for strings.");
                case CoreBinaryInstruction binary
                    when binary.Type.IsString || registerTypes[binary.Left].IsString || registerTypes[binary.Right].IsString:
                {
                    var left = registerTypes[binary.Left];
                    var right = registerTypes[binary.Right];
                    var valid = binary.Operator == IrBinaryOperator.Add &&
                            binary.Type.IsString && left.IsString && right.IsString ||
                        binary.Operator is IrBinaryOperator.Equal or IrBinaryOperator.NotEqual &&
                            binary.Type.IsBoolean && left.IsString && right.IsString;
                    if (!valid)
                        return new(null, $"Binary operator '{binary.Operator}' has an unsupported string operand shape.");
                    break;
                }
            }
        }
        var missingCall = core.Blocks.SelectMany(block => block.Instructions)
            .OfType<CoreCallInstruction>()
            .FirstOrDefault(call => methodIds is null || !methodIds.ContainsKey(call.Target));
        if (missingCall is not null)
            return new(null, $"Call target '{missingCall.Target.Value}' is not part of the register-bytecode module.");

        var offsets = new Dictionary<CoreBlockId, BytecodeOffset>();
        var nextOffset = 0;
        foreach (var block in core.Blocks)
        {
            offsets[block.Id] = new BytecodeOffset(nextOffset);
            nextOffset += block.Instructions.Count + 1;
        }

        var instructions = new List<RegisterBytecodeInstruction>(nextOffset);
        foreach (var block in core.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                instructions.Add(instruction switch
                {
                    CoreConstantInstruction constant => new BytecodeConstantInstruction(
                        Register(constant.Result), constant.Type, constant.Value),
                    CoreMoveInstruction move => new BytecodeMoveInstruction(
                        Register(move.Result), Register(move.Operand)),
                    CoreConvertInstruction convert => new BytecodeConvertInstruction(
                        Register(convert.Result), convert.Type, Register(convert.Operand)),
                    CoreUnaryInstruction unary => new BytecodeUnaryInstruction(
                        Register(unary.Result), unary.Type, unary.Operator, Register(unary.Operand)),
                    CoreBinaryInstruction binary => new BytecodeBinaryInstruction(
                        Register(binary.Result), binary.Type, binary.Operator,
                        Register(binary.Left), Register(binary.Right)),
                    CoreCallInstruction call => new BytecodeCallInstruction(
                            call.Type == IrType.Void ? null : Register(call.Result),
                            methodIds![call.Target],
                            call.Arguments.Select(Register).ToArray()),
                    CoreHostCallInstruction host => new BytecodeHostCallInstruction(
                        (BytecodeHostOperation)host.Operation,
                        host.Arguments.Select(Register).ToArray()),
                    _ => throw new InvalidOperationException($"Unknown Core IR instruction '{instruction.GetType().Name}'.")
                });
            }

            instructions.Add(block.Terminator switch
            {
                CoreJump jump => new BytecodeBranchInstruction(null, offsets[jump.Target], null),
                CoreBranch branch => new BytecodeBranchInstruction(
                    Register(branch.Condition), offsets[branch.WhenTrue], offsets[branch.WhenFalse]),
                CoreReturn @return => new BytecodeReturnInstruction(
                    @return.Value is { } value ? Register(value) : null),
                _ => throw new InvalidOperationException($"Unknown Core IR terminator '{block.Terminator.GetType().Name}'.")
            });
        }

        var method = new RegisterBytecodeMethod(
            methodId,
            source.Id,
            source.Name,
            core.ReturnType,
            core.Parameters.Select(parameter => new RegisterBytecodeParameter(
                Register(parameter.Value), parameter.Type)).ToArray(),
            registerTypes.OrderBy(pair => pair.Key.Value)
                .Select(pair => new RegisterBytecodeRegister(Register(pair.Key), pair.Value)).ToArray(),
            instructions);
        return new(method, null);
    }

    private static BytecodeRegister Register(CoreValueId value) => new(value.Value);

}

internal static class RegisterBytecodeDisassembler
{
    public static string Disassemble(RegisterBytecodeMethod method)
    {
        var text = new StringBuilder();
        text.Append("method ").Append(method.Id.Value).Append(' ').Append(method.Name).AppendLine();
        for (var index = 0; index < method.Instructions.Count; index++)
        {
            text.Append(index.ToString("D4", CultureInfo.InvariantCulture)).Append(": ")
                .AppendLine(Format(method.Instructions[index]));
        }
        return text.ToString();
    }

    private static string Format(RegisterBytecodeInstruction instruction) => instruction switch
    {
        BytecodeConstantInstruction value => $"const r{value.Destination.Value}, {FormatConstant(value.Value)}",
        BytecodeMoveInstruction move => $"move r{move.Destination.Value}, r{move.Source.Value}",
        BytecodeConvertInstruction convert => $"convert.{convert.Type.Name} r{convert.Destination.Value}, r{convert.Source.Value}",
        BytecodeUnaryInstruction unary => $"unary.{unary.Operator} r{unary.Destination.Value}, r{unary.Operand.Value}",
        BytecodeBinaryInstruction binary => $"binary.{binary.Operator} r{binary.Destination.Value}, r{binary.Left.Value}, r{binary.Right.Value}",
        BytecodeBranchInstruction { Condition: null } branch => $"branch {branch.WhenTrue.Value}",
        BytecodeBranchInstruction branch => $"branch r{branch.Condition!.Value.Value}, {branch.WhenTrue.Value}, {branch.WhenFalse!.Value.Value}",
        BytecodeCallInstruction call => $"call {call.Target.Value} ({string.Join(", ", call.Arguments.Select(value => $"r{value.Value}"))})",
        BytecodeHostCallInstruction call => $"host.{call.Operation} ({string.Join(", ", call.Arguments.Select(value => $"r{value.Value}"))})",
        BytecodeReturnInstruction { Value: null } => "return",
        BytecodeReturnInstruction value => $"return r{value.Value!.Value.Value}",
        _ => instruction.OpCode.ToString()
    };

    private static string FormatConstant(object? value) => value switch
    {
        null => "null",
        bool boolean => boolean ? "true" : "false",
        string text => $"\"{EscapeString(text)}\"",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "null"
    };

    private static string EscapeString(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);
}
