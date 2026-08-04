using Xunit;

namespace SharpSql.Tests;

public sealed class CoreIrLowererTests
{
    [Fact]
    public void LowersScalarControlFlowToCompactBlocks()
    {
        const string source = """
            int marker = 0;

            int SumTo(int limit)
            {
                int sum = 0;
                while (limit > 0)
                {
                    sum += limit;
                    limit--;
                }
                return sum;
            }
            """;
        var compiler = new SharpSqlCompiler();

        var transpilation = compiler.Transpile(source);
        var method = Assert.Single(Assert.IsType<IrProgram>(compiler.BoundProgram).Methods);
        var result = CoreIrLowerer.Lower(method);

        Assert.True(transpilation.Success, string.Join(Environment.NewLine, transpilation.Diagnostics));
        var core = Assert.IsType<CoreMethod>(result.Method);
        Assert.Null(result.UnsupportedReason);
        Assert.Single(core.Parameters);
        Assert.Single(core.Locals);
        Assert.Equal(new CoreBlockId(0), core.EntryBlock);
        Assert.Contains(core.Blocks, block => block.Terminator is CoreBranch);
        Assert.Contains(core.Blocks, block =>
            block.Terminator is CoreJump jump && jump.Target.Value <= block.Id.Value);
        Assert.Contains(
            core.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction is CoreBinaryInstruction { Operator: IrBinaryOperator.Add });
        Assert.DoesNotContain(
            typeof(CoreMethod).GetProperties(),
            property => property.PropertyType == typeof(IrSource));
    }

    [Fact]
    public void RejectsRichOperationsInsteadOfLeakingThemIntoCoreIr()
    {
        const string source = """
            int marker = 0;

            int ReadValue()
            {
                return System.Random.Shared.Next();
            }
            """;
        var compiler = new SharpSqlCompiler();

        compiler.Transpile(source);
        var method = Assert.Single(Assert.IsType<IrProgram>(compiler.BoundProgram).Methods);
        var result = CoreIrLowerer.Lower(method);

        Assert.False(result.Success);
        Assert.Null(result.Method);
        Assert.Contains(nameof(IrInvocationExpression), result.UnsupportedReason);
    }

    [Fact]
    public void LowersTheRegisterBytecodeStringSliceWithoutNewCoreOperations()
    {
        const string source = """
            int marker = 0;
            string Work(string value)
            {
                string missing = default;
                string result = "[" + value + "]";
                Console.WriteLine(missing);
                return result;
            }
            """;
        var compiler = new SharpSqlCompiler();

        compiler.Transpile(source);
        var method = Assert.Single(Assert.IsType<IrProgram>(compiler.BoundProgram).Methods);
        var result = CoreIrLowerer.Lower(method);

        var core = Assert.IsType<CoreMethod>(result.Method);
        Assert.Contains(core.Parameters, parameter => parameter.Type == IrType.String);
        Assert.Contains(core.Blocks.SelectMany(block => block.Instructions), instruction =>
            instruction is CoreConstantInstruction { Type: { IsString: true }, Value: null });
        Assert.Equal(2, core.Blocks.SelectMany(block => block.Instructions)
            .Count(instruction => instruction is CoreBinaryInstruction { Operator: IrBinaryOperator.Add }));
        Assert.Contains(core.Blocks.SelectMany(block => block.Instructions), instruction =>
            instruction is CoreHostCallInstruction { Operation: CoreHostOperation.WriteLine });
    }
}
