using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SharpSql;

internal sealed class RegisterBytecodeImage
{
    private RegisterBytecodeImage(
        RegisterBytecodeModule module,
        byte[] id,
        int instructionCount,
        int argumentCount,
        int parameterCount)
    {
        Module = module;
        Id = id;
        InstructionCount = instructionCount;
        ArgumentCount = argumentCount;
        ParameterCount = parameterCount;
    }

    public RegisterBytecodeModule Module { get; }
    public byte[] Id { get; }
    public int InstructionCount { get; }
    public int ArgumentCount { get; }
    public int ParameterCount { get; }
    public string HexId => string.Concat(Id.Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
    public string SqlId => "0x" + HexId;

    public static RegisterBytecodeImage Create(RegisterBytecodeModule module)
    {
        var validation = RegisterBytecodeContract.Validate(module);
        if (validation.Count != 0)
        {
            throw new InvalidOperationException(
                "Cannot create a durable image from invalid register bytecode: " +
                string.Join(" ", validation.Select(error => error.Message)));
        }

        var instructionCount = module.Methods.Sum(method => method.Instructions.Count);
        var argumentCount = module.Methods.Sum(method => method.Instructions.Sum(instruction =>
            instruction is BytecodeCallInstruction call
                ? call.Arguments.Count
                : instruction is BytecodeHostCallInstruction host ? host.Arguments.Count : 0));
        var parameterCount = module.Methods.Sum(method => method.Parameters.Count);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(module.Version.Major);
            writer.Write(module.Version.Minor);
            var methods = module.Methods.OrderBy(method => method.Id.Value).ToArray();
            writer.Write(methods.Length);
            foreach (var method in methods)
                WriteMethod(writer, method);
        }

        using var algorithm = SHA256.Create();
        return new RegisterBytecodeImage(
            module,
            algorithm.ComputeHash(stream.ToArray()),
            instructionCount,
            argumentCount,
            parameterCount);
    }

    private static void WriteMethod(BinaryWriter writer, RegisterBytecodeMethod method)
    {
        writer.Write(method.Id.Value);
        writer.Write(TypeCode(method.ReturnType));

        writer.Write(method.Parameters.Count);
        foreach (var parameter in method.Parameters)
        {
            writer.Write(parameter.Register.Value);
            writer.Write(TypeCode(parameter.Type));
        }

        var registers = method.Registers.OrderBy(register => register.Register.Value).ToArray();
        writer.Write(registers.Length);
        foreach (var register in registers)
        {
            writer.Write(register.Register.Value);
            writer.Write(TypeCode(register.Type));
        }

        writer.Write(method.Instructions.Count);
        foreach (var instruction in method.Instructions)
            WriteInstruction(writer, instruction);
    }

    private static void WriteInstruction(BinaryWriter writer, RegisterBytecodeInstruction instruction)
    {
        writer.Write((byte)instruction.OpCode);
        switch (instruction)
        {
            case BytecodeConstantInstruction constant:
                writer.Write(constant.Destination.Value);
                writer.Write(TypeCode(constant.Type));
                WriteConstant(writer, constant.Value);
                break;
            case BytecodeMoveInstruction move:
                writer.Write(move.Destination.Value);
                writer.Write(move.Source.Value);
                break;
            case BytecodeConvertInstruction convert:
                writer.Write(convert.Destination.Value);
                writer.Write(TypeCode(convert.Type));
                writer.Write(convert.Source.Value);
                break;
            case BytecodeUnaryInstruction unary:
                writer.Write(unary.Destination.Value);
                writer.Write(TypeCode(unary.Type));
                writer.Write((int)unary.Operator);
                writer.Write(unary.Operand.Value);
                break;
            case BytecodeBinaryInstruction binary:
                writer.Write(binary.Destination.Value);
                writer.Write(TypeCode(binary.Type));
                writer.Write((int)binary.Operator);
                writer.Write(binary.Left.Value);
                writer.Write(binary.Right.Value);
                break;
            case BytecodeBranchInstruction branch:
                WriteNullableInt(writer, branch.Condition?.Value);
                writer.Write(branch.WhenTrue.Value);
                WriteNullableInt(writer, branch.WhenFalse?.Value);
                break;
            case BytecodeCallInstruction call:
                WriteNullableInt(writer, call.Destination?.Value);
                writer.Write(call.Target.Value);
                writer.Write(call.Arguments.Count);
                foreach (var argument in call.Arguments)
                    writer.Write(argument.Value);
                break;
            case BytecodeHostCallInstruction host:
                writer.Write((int)host.Operation);
                writer.Write(host.Arguments.Count);
                foreach (var argument in host.Arguments)
                    writer.Write(argument.Value);
                break;
            case BytecodeReturnInstruction @return:
                WriteNullableInt(writer, @return.Value?.Value);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown register bytecode instruction '{instruction.GetType().Name}'.");
        }
    }

    private static void WriteConstant(BinaryWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.Write((byte)0);
                break;
            case bool boolean:
                writer.Write((byte)1);
                writer.Write(boolean);
                break;
            case int integer:
                writer.Write((byte)2);
                writer.Write(integer);
                break;
            case long integer:
                writer.Write((byte)3);
                writer.Write(integer);
                break;
            case string text:
                writer.Write((byte)4);
                writer.Write(text);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported register-bytecode constant '{value}'.");
        }
    }

    private static void WriteNullableInt(BinaryWriter writer, int? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue)
            writer.Write(value.Value);
    }

    private static byte TypeCode(IrType type) => type.Name switch
    {
        "void" => 0,
        "bool" => 1,
        "int" => 2,
        "long" => 3,
        "string" => 4,
        _ => throw new InvalidOperationException(
            $"Unsupported register-bytecode type '{type.Name}'.")
    };
}
