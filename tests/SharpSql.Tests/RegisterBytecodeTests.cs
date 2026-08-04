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
}
