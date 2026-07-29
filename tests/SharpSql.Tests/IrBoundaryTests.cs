using System.Reflection;
using Xunit;

namespace SharpSql.Tests;

public sealed class IrBoundaryTests
{
    [Fact]
    public void CSharpFrontendBindsACompleteTypedEntryPointBeforeSqlLowering()
    {
        var compiler = new SharpSqlCompiler();

        var result = compiler.Transpile("int total = 1 + 2; total += 3;");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var program = Assert.IsType<IrProgram>(compiler.BoundProgram);
        var declaration = Assert.IsType<ProceduralDeclarationStatement>(program.EntryPoint.Statements[0]);
        var variable = Assert.Single(declaration.Declaration.Variables);
        Assert.Equal("total", variable.Symbol.Name);
        Assert.Equal(IrType.Int, variable.Symbol.Type);
        Assert.IsType<IrBinaryExpression>(variable.Initializer);
        var mutation = Assert.IsType<ProceduralExpressionStatement>(program.EntryPoint.Statements[1]);
        Assert.Equal(IrAssignmentOperator.Add, Assert.IsType<IrAssignmentExpression>(mutation.Expression).Operator);
    }

    [Fact]
    public void SqlBackendAcceptsAProgramConstructedWithoutRoslyn()
    {
        var source = IrSource.None;
        var intFacts = new ExpressionFacts(IrType.Int, ScalarNullability.NonNull, false, null);
        var symbol = new IrSymbol(new IrSymbolId(1), "answer", IrType.Int);
        var one = new IrConstantExpression(source, intFacts, 1, "1");
        var two = new IrConstantExpression(source, intFacts, 2, "2");
        var sum = new IrBinaryExpression(source, intFacts, IrBinaryOperator.Add, one, two);
        var declaration = new ProceduralDeclarationStatement(
            source,
            new ProceduralDeclaration(
                source,
                [new ProceduralVariable(source, symbol, sum)]));
        var assignment = new ProceduralExpressionStatement(
            source,
            new IrAssignmentExpression(
                source,
                intFacts,
                IrAssignmentOperator.Add,
                new IrVariableExpression(source, intFacts, symbol),
                two));
        var program = new IrProgram(
            Array.Empty<MethodDefinition>(),
            new ProceduralBlock(source, [declaration, assignment]),
            Array.Empty<IrComment>());

        var result = new SharpSqlCompiler().Transpile(program);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("DECLARE @answer INT = 1 + 2;", result.Sql);
        Assert.Contains("SET @answer = @answer + 2;", result.Sql);
    }

    [Fact]
    public void CompilerIrContractContainsNeitherRoslynNodesNorSqlPayloads()
    {
        var assembly = typeof(IrProgram).Assembly;
        Assert.DoesNotContain(
            assembly.GetReferencedAssemblies(),
            reference => reference.Name?.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) == true);
        var contractTypes = assembly.GetTypes().Where(type =>
            type == typeof(IrType) ||
            type == typeof(MethodDefinition) ||
            type == typeof(ParameterDefinition) ||
            type.Name.StartsWith("Ir", StringComparison.Ordinal) ||
            type.Name.StartsWith("Procedural", StringComparison.Ordinal));

        foreach (var type in contractTypes)
        {
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                Assert.False(ContainsRoslynType(property.PropertyType),
                    $"{type.Name}.{property.Name} leaks Roslyn type {property.PropertyType}.");
                Assert.NotEqual("Sql", property.Name);
                Assert.False(property.Name.EndsWith("Sql", StringComparison.Ordinal));
            }
        }

        static bool ContainsRoslynType(Type type)
        {
            if (type.Namespace?.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) == true)
                return true;
            return type.IsGenericType && type.GetGenericArguments().Any(ContainsRoslynType);
        }
    }
}
