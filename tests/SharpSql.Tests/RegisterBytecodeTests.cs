using Xunit;

namespace SharpSql.Tests;

public sealed class RegisterBytecodeTests
{
    [Fact]
    public void LowersCoreBlocksToEightFamilyRegisterBytecodeDeterministically()
    {
        const string source = """
            int marker = 0;
            int Sum(int value)
            {
                int result = 0;
                while (value > 0)
                {
                    result += value;
                    value--;
                }
                return result;
            }
            """;
        var compiler = new SharpSqlCompiler();
        compiler.Transpile(source, new TranspileOptions { ManagedFallback = ManagedFallbackKind.Legacy });
        var definition = Assert.Single(Assert.IsType<IrProgram>(compiler.BoundProgram).Methods);
        var core = Assert.IsType<CoreMethod>(CoreIrLowerer.Lower(definition).Method);

        var result = RegisterBytecodeLowerer.Lower(definition, core, new BytecodeMethodId(1));

        var method = Assert.IsType<RegisterBytecodeMethod>(result.Method);
        Assert.Null(result.UnsupportedReason);
        Assert.Empty(RegisterBytecodeContract.Validate(new RegisterBytecodeModule(
            RegisterBytecodeContract.CurrentVersion,
            [method])));
        Assert.Contains(method.Instructions, instruction => instruction is BytecodeBranchInstruction);
        Assert.Contains(method.Instructions, instruction => instruction is BytecodeBinaryInstruction);
        Assert.EndsWith("return r2\n", RegisterBytecodeDisassembler.Disassemble(method), StringComparison.Ordinal);
        Assert.Equal(8, Enum.GetValues<RegisterBytecodeOpCode>().Length);
    }

    [Fact]
    public void ValidatorRejectsRegistersAndBranchesOutsideTheMethod()
    {
        var method = new RegisterBytecodeMethod(
            new BytecodeMethodId(1),
            new IrMethodId("M"),
            "M",
            IrType.Int,
            [],
            [new RegisterBytecodeRegister(new BytecodeRegister(1), IrType.Int)],
            [
                new BytecodeMoveInstruction(new BytecodeRegister(1), new BytecodeRegister(2)),
                new BytecodeBranchInstruction(null, new BytecodeOffset(9), null)
            ]);

        var errors = RegisterBytecodeContract.Validate(new RegisterBytecodeModule(
            RegisterBytecodeContract.CurrentVersion,
            [method]));

        Assert.Contains(errors, error => error.Code == RegisterBytecodeValidationCode.InvalidRegister);
        Assert.Contains(errors, error => error.Code == RegisterBytecodeValidationCode.InvalidBranch);
    }

    [Fact]
    public void LinksTypedDirectCallsAcrossABytecodeModule()
    {
        const string source = """
            int marker = 0;
            int Step(int value) => value + 1;
            int Twice(int value) => Step(Step(value));
            """;
        var compiler = new SharpSqlCompiler();
        compiler.Transpile(source, new TranspileOptions { ManagedFallback = ManagedFallbackKind.Legacy });
        var definitions = Assert.IsType<IrProgram>(compiler.BoundProgram).Methods
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .ToArray();
        var ids = definitions.Select((method, index) =>
                (Source: method.Id, Bytecode: new BytecodeMethodId(index + 1)))
            .ToDictionary(item => item.Source, item => item.Bytecode);
        var methods = definitions.Select(method => Assert.IsType<RegisterBytecodeMethod>(
            RegisterBytecodeLowerer.Lower(
                method,
                Assert.IsType<CoreMethod>(CoreIrLowerer.Lower(method, ids.Keys.ToArray()).Method),
                ids[method.Id],
                ids).Method)).ToArray();

        var validation = RegisterBytecodeContract.Validate(new RegisterBytecodeModule(
            RegisterBytecodeContract.CurrentVersion,
            methods));

        Assert.Empty(validation);
        Assert.Equal(2, methods.Single(method => method.Name == "Twice").Instructions
            .Count(instruction => instruction is BytecodeCallInstruction));
    }

    [Fact]
    public void LowersVoidDirectAndRecursiveCallsWithoutResultRegisters()
    {
        const string source = """
            int marker = 0;
            void Emit(int value) => Console.WriteLine(value);
            void Countdown(int value)
            {
                if (value == 0)
                    return;
                Emit(value);
                Countdown(value - 1);
            }
            """;
        var compiler = new SharpSqlCompiler();
        compiler.Transpile(source, new TranspileOptions { ManagedFallback = ManagedFallbackKind.Legacy });
        var definitions = Assert.IsType<IrProgram>(compiler.BoundProgram).Methods.ToArray();
        var ids = definitions.Select((method, index) =>
                (Source: method.Id, Bytecode: new BytecodeMethodId(index + 1)))
            .ToDictionary(item => item.Source, item => item.Bytecode);
        var methods = definitions.Select(method => Assert.IsType<RegisterBytecodeMethod>(
            RegisterBytecodeLowerer.Lower(
                method,
                Assert.IsType<CoreMethod>(CoreIrLowerer.Lower(method, ids.Keys.ToArray()).Method),
                ids[method.Id],
                ids).Method)).ToArray();

        var calls = methods.Single(method => method.Name == "Countdown").Instructions
            .OfType<BytecodeCallInstruction>()
            .ToArray();

        Assert.Equal(2, calls.Length);
        Assert.All(calls, call => Assert.Null(call.Destination));
        Assert.DoesNotContain(methods.SelectMany(method => method.Registers), register => register.Type == IrType.Void);
        Assert.Empty(RegisterBytecodeContract.Validate(new RegisterBytecodeModule(
            RegisterBytecodeContract.CurrentVersion,
            methods)));
    }

    [Fact]
    public void LowersStringConstantsCallsConcatenationAndEquality()
    {
        const string source = """
            int marker = 0;
            string Echo(string value) => value;
            string Decorate(string value)
            {
                string missing = default;
                Console.WriteLine(missing);
                return "[" + Echo(value) + "]";
            }
            bool Same(string left, string right) => left == right;
            """;
        var compiler = new SharpSqlCompiler();
        compiler.Transpile(source, new TranspileOptions { ManagedFallback = ManagedFallbackKind.Legacy });
        var definitions = Assert.IsType<IrProgram>(compiler.BoundProgram).Methods.ToArray();
        var ids = definitions.Select((method, index) =>
                (Source: method.Id, Bytecode: new BytecodeMethodId(index + 1)))
            .ToDictionary(item => item.Source, item => item.Bytecode);
        var methods = definitions.Select(method => Assert.IsType<RegisterBytecodeMethod>(
            RegisterBytecodeLowerer.Lower(
                method,
                Assert.IsType<CoreMethod>(CoreIrLowerer.Lower(method, ids.Keys.ToArray()).Method),
                ids[method.Id],
                ids).Method)).ToArray();

        var decorate = methods.Single(method => method.Name == "Decorate");
        var same = methods.Single(method => method.Name == "Same");

        Assert.Contains(decorate.Registers, register => register.Type == IrType.String);
        Assert.Contains(decorate.Instructions, instruction =>
            instruction is BytecodeConstantInstruction { Type: { IsString: true }, Value: null });
        var writeLine = Assert.Single(decorate.Instructions.OfType<BytecodeHostCallInstruction>());
        Assert.Equal(BytecodeHostOperation.WriteLine, writeLine.Operation);
        Assert.Equal(
            IrType.String,
            decorate.Registers.Single(register => register.Register == Assert.Single(writeLine.Arguments)).Type);
        Assert.Equal(2, decorate.Instructions.Count(instruction =>
            instruction is BytecodeBinaryInstruction { Operator: IrBinaryOperator.Add }));
        Assert.Contains(same.Instructions, instruction =>
            instruction is BytecodeBinaryInstruction { Operator: IrBinaryOperator.Equal, Type: { IsBoolean: true } });
        Assert.Contains("const r", RegisterBytecodeDisassembler.Disassemble(decorate), StringComparison.Ordinal);
        Assert.Contains("\"[\"", RegisterBytecodeDisassembler.Disassemble(decorate), StringComparison.Ordinal);
        Assert.Empty(RegisterBytecodeContract.Validate(new RegisterBytecodeModule(
            RegisterBytecodeContract.CurrentVersion,
            methods)));
        Assert.Equal(8, Enum.GetValues<RegisterBytecodeOpCode>().Length);
    }

    [Fact]
    public void ValidatorRejectsInvalidStringValuesAndReturnTypes()
    {
        var method = new RegisterBytecodeMethod(
            new BytecodeMethodId(1),
            new IrMethodId("Text"),
            "Text",
            IrType.String,
            [],
            [
                new RegisterBytecodeRegister(new BytecodeRegister(1), IrType.String),
                new RegisterBytecodeRegister(new BytecodeRegister(2), IrType.Int)
            ],
            [
                new BytecodeConstantInstruction(new BytecodeRegister(1), IrType.String, 1),
                new BytecodeReturnInstruction(new BytecodeRegister(2))
            ]);

        var errors = RegisterBytecodeContract.Validate(new RegisterBytecodeModule(
            RegisterBytecodeContract.CurrentVersion,
            [method]));

        Assert.Contains(errors, error => error.Code == RegisterBytecodeValidationCode.InvalidInstruction);
        Assert.Contains(errors, error => error.Code == RegisterBytecodeValidationCode.InvalidReturn);
    }
}
