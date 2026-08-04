namespace SharpSql;

// The compact managed fallback is deliberately register based: Core IR values map
// one-to-one to registers, so lowering does not manufacture load/store traffic.
internal readonly record struct RegisterBytecodeVersion(int Major, int Minor);
internal readonly record struct BytecodeMethodId(int Value);
internal readonly record struct BytecodeRegister(int Value);
internal readonly record struct BytecodeOffset(int Value);

internal enum RegisterBytecodeOpCode
{
    Constant = 1,
    Move = 2,
    Convert = 3,
    Unary = 4,
    Binary = 5,
    Branch = 6,
    Call = 7,
    Return = 8
}

internal sealed record RegisterBytecodeParameter(BytecodeRegister Register, IrType Type);
internal sealed record RegisterBytecodeRegister(BytecodeRegister Register, IrType Type);

internal abstract record RegisterBytecodeInstruction(RegisterBytecodeOpCode OpCode);

internal sealed record BytecodeConstantInstruction(
    BytecodeRegister Destination,
    IrType Type,
    object? Value) : RegisterBytecodeInstruction(RegisterBytecodeOpCode.Constant);

internal sealed record BytecodeMoveInstruction(
    BytecodeRegister Destination,
    BytecodeRegister Source) : RegisterBytecodeInstruction(RegisterBytecodeOpCode.Move);

internal sealed record BytecodeConvertInstruction(
    BytecodeRegister Destination,
    IrType Type,
    BytecodeRegister Source) : RegisterBytecodeInstruction(RegisterBytecodeOpCode.Convert);

internal sealed record BytecodeUnaryInstruction(
    BytecodeRegister Destination,
    IrType Type,
    IrUnaryOperator Operator,
    BytecodeRegister Operand) : RegisterBytecodeInstruction(RegisterBytecodeOpCode.Unary);

internal sealed record BytecodeBinaryInstruction(
    BytecodeRegister Destination,
    IrType Type,
    IrBinaryOperator Operator,
    BytecodeRegister Left,
    BytecodeRegister Right) : RegisterBytecodeInstruction(RegisterBytecodeOpCode.Binary);

internal sealed record BytecodeBranchInstruction(
    BytecodeRegister? Condition,
    BytecodeOffset WhenTrue,
    BytecodeOffset? WhenFalse) : RegisterBytecodeInstruction(RegisterBytecodeOpCode.Branch);

internal sealed record BytecodeCallInstruction(
    BytecodeRegister? Destination,
    BytecodeMethodId Target,
    IReadOnlyList<BytecodeRegister> Arguments) : RegisterBytecodeInstruction(RegisterBytecodeOpCode.Call);

internal enum BytecodeHostOperation
{
    WriteLine = 1
}

internal sealed record BytecodeHostCallInstruction(
    BytecodeHostOperation Operation,
    IReadOnlyList<BytecodeRegister> Arguments) : RegisterBytecodeInstruction(RegisterBytecodeOpCode.Call);

internal sealed record BytecodeReturnInstruction(
    BytecodeRegister? Value) : RegisterBytecodeInstruction(RegisterBytecodeOpCode.Return);

internal sealed record RegisterBytecodeMethod(
    BytecodeMethodId Id,
    IrMethodId SourceMethod,
    string Name,
    IrType ReturnType,
    IReadOnlyList<RegisterBytecodeParameter> Parameters,
    IReadOnlyList<RegisterBytecodeRegister> Registers,
    IReadOnlyList<RegisterBytecodeInstruction> Instructions);

internal sealed record RegisterBytecodeModule(
    RegisterBytecodeVersion Version,
    IReadOnlyList<RegisterBytecodeMethod> Methods);

internal enum RegisterBytecodeValidationCode
{
    UnsupportedVersion,
    DuplicateMethod,
    InvalidMethod,
    DuplicateRegister,
    InvalidRegister,
    InvalidBranch,
    InvalidCall,
    InvalidReturn,
    UnsupportedType
}

internal sealed record RegisterBytecodeValidationError(
    RegisterBytecodeValidationCode Code,
    string Message,
    BytecodeMethodId? Method = null,
    BytecodeOffset? Instruction = null);

internal static class RegisterBytecodeContract
{
    public static RegisterBytecodeVersion CurrentVersion { get; } = new(1, 1);

    public static IReadOnlyList<RegisterBytecodeValidationError> Validate(RegisterBytecodeModule module)
    {
        if (module is null)
            throw new ArgumentNullException(nameof(module));
        var errors = new List<RegisterBytecodeValidationError>();
        if (module.Version != CurrentVersion)
        {
            errors.Add(new(
                RegisterBytecodeValidationCode.UnsupportedVersion,
                $"Register bytecode {module.Version.Major}.{module.Version.Minor} is not supported; expected {CurrentVersion.Major}.{CurrentVersion.Minor}."));
        }

        var methods = new Dictionary<BytecodeMethodId, RegisterBytecodeMethod>();
        foreach (var method in module.Methods)
        {
            if (method.Id.Value <= 0 || methods.ContainsKey(method.Id))
            {
                errors.Add(new(
                    method.Id.Value <= 0
                        ? RegisterBytecodeValidationCode.InvalidMethod
                        : RegisterBytecodeValidationCode.DuplicateMethod,
                    $"Bytecode method ID {method.Id.Value} must be positive and unique.",
                    method.Id));
            }
            else
                methods.Add(method.Id, method);
        }

        foreach (var method in module.Methods)
            ValidateMethod(method, methods, errors);
        return errors;
    }

    private static void ValidateMethod(
        RegisterBytecodeMethod method,
        IReadOnlyDictionary<BytecodeMethodId, RegisterBytecodeMethod> methods,
        List<RegisterBytecodeValidationError> errors)
    {
        var registers = new Dictionary<BytecodeRegister, IrType>();
        foreach (var register in method.Registers)
        {
            if (register.Register.Value <= 0 || registers.ContainsKey(register.Register))
            {
                errors.Add(new(
                    register.Register.Value <= 0
                        ? RegisterBytecodeValidationCode.InvalidRegister
                        : RegisterBytecodeValidationCode.DuplicateRegister,
                    $"Register r{register.Register.Value} must be positive and unique.",
                    method.Id));
            }
            else
                registers.Add(register.Register, register.Type);
            if (!IsRuntimeType(register.Type))
            {
                errors.Add(new(
                    RegisterBytecodeValidationCode.UnsupportedType,
                    $"Register r{register.Register.Value} has unsupported runtime type '{register.Type.Name}'.",
                    method.Id));
            }
        }

        foreach (var parameter in method.Parameters)
        {
            if (!registers.TryGetValue(parameter.Register, out var type) || type != parameter.Type)
            {
                errors.Add(new(
                    RegisterBytecodeValidationCode.InvalidRegister,
                    $"Parameter register r{parameter.Register.Value} is missing or has the wrong type.",
                    method.Id));
            }
        }

        for (var index = 0; index < method.Instructions.Count; index++)
            ValidateInstruction(method, new BytecodeOffset(index), method.Instructions[index], registers, methods, errors);
    }

    private static void ValidateInstruction(
        RegisterBytecodeMethod method,
        BytecodeOffset offset,
        RegisterBytecodeInstruction instruction,
        IReadOnlyDictionary<BytecodeRegister, IrType> registers,
        IReadOnlyDictionary<BytecodeMethodId, RegisterBytecodeMethod> methods,
        List<RegisterBytecodeValidationError> errors)
    {
        void Require(BytecodeRegister register)
        {
            if (!registers.ContainsKey(register))
                errors.Add(new(RegisterBytecodeValidationCode.InvalidRegister,
                    $"Instruction {offset.Value} references missing register r{register.Value}.", method.Id, offset));
        }
        void Target(BytecodeOffset target)
        {
            if (target.Value < 0 || target.Value >= method.Instructions.Count)
                errors.Add(new(RegisterBytecodeValidationCode.InvalidBranch,
                    $"Instruction {offset.Value} branches outside the method to {target.Value}.", method.Id, offset));
        }

        switch (instruction)
        {
            case BytecodeConstantInstruction constant:
                Require(constant.Destination);
                break;
            case BytecodeMoveInstruction move:
                Require(move.Destination);
                Require(move.Source);
                break;
            case BytecodeConvertInstruction convert:
                Require(convert.Destination);
                Require(convert.Source);
                break;
            case BytecodeUnaryInstruction unary:
                Require(unary.Destination);
                Require(unary.Operand);
                break;
            case BytecodeBinaryInstruction binary:
                Require(binary.Destination);
                Require(binary.Left);
                Require(binary.Right);
                break;
            case BytecodeBranchInstruction branch:
                if (branch.Condition is { } condition)
                    Require(condition);
                Target(branch.WhenTrue);
                if (branch.WhenFalse is { } whenFalse)
                    Target(whenFalse);
                if (branch.Condition is null != (branch.WhenFalse is null))
                    errors.Add(new(RegisterBytecodeValidationCode.InvalidBranch,
                        "A conditional branch requires both a condition and false target; an unconditional branch requires neither.",
                        method.Id, offset));
                break;
            case BytecodeCallInstruction call:
                if (call.Destination is { } destination)
                    Require(destination);
                foreach (var argument in call.Arguments)
                    Require(argument);
                if (!methods.TryGetValue(call.Target, out var target) || target.Parameters.Count != call.Arguments.Count)
                    errors.Add(new(RegisterBytecodeValidationCode.InvalidCall,
                        $"Call target {call.Target.Value} is missing or has a different arity.", method.Id, offset));
                else
                {
                    for (var index = 0; index < call.Arguments.Count; index++)
                    {
                        if (registers.TryGetValue(call.Arguments[index], out var argumentType) &&
                            argumentType != target.Parameters[index].Type)
                        {
                            errors.Add(new(RegisterBytecodeValidationCode.InvalidCall,
                                $"Call argument {index} has type '{argumentType.Name}', expected '{target.Parameters[index].Type.Name}'.",
                                method.Id, offset));
                        }
                    }
                    if (target.ReturnType == IrType.Void && call.Destination is not null ||
                        target.ReturnType != IrType.Void &&
                        (call.Destination is null ||
                         registers.TryGetValue(call.Destination.Value, out var destinationType) && destinationType != target.ReturnType))
                    {
                        errors.Add(new(RegisterBytecodeValidationCode.InvalidCall,
                            "Call destination does not match the target return type.", method.Id, offset));
                    }
                }
                break;
            case BytecodeHostCallInstruction host:
                foreach (var argument in host.Arguments)
                    Require(argument);
                if (host.Operation != BytecodeHostOperation.WriteLine || host.Arguments.Count > 1)
                {
                    errors.Add(new(RegisterBytecodeValidationCode.InvalidCall,
                        $"Host operation '{host.Operation}' has an invalid argument shape.", method.Id, offset));
                }
                break;
            case BytecodeReturnInstruction @return:
                if (@return.Value is { } value)
                    Require(value);
                if ((@return.Value is null) != (method.ReturnType == IrType.Void))
                    errors.Add(new(RegisterBytecodeValidationCode.InvalidReturn,
                        "Return value presence does not match the method return type.", method.Id, offset));
                break;
        }
    }

    public static bool IsRuntimeType(IrType type) => type.Name is "bool" or "int" or "long";
}
