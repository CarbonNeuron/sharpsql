using System.Reflection;
using Xunit;

namespace SharpSql.Tests;

public sealed class IrBoundaryTests
{
    [Fact]
    public void MethodGraphCentralizesCallCountsRecursionAndVmClosure()
    {
        var source = IrSource.None;
        var facts = new ExpressionFacts(IrType.Int, ScalarNullability.NonNull, false, null);
        IrInvocationExpression Call(string name) => new(
            source,
            facts,
            new IrVariableExpression(source, facts, new IrSymbol(IrSymbolId.None, name, IrType.Unknown)),
            []);
        MethodDefinition Method(string name, string callee) => new(
            name,
            IrType.Int,
            [],
            null,
            Call(callee),
            source);
        var methods = new[]
        {
            Method("First", "Second"),
            Method("Second", "First"),
            Method("Caller", "First")
        };
        var entry = new ProceduralBlock(
            source,
            [new ProceduralExpressionStatement(source, Call("Caller"))]);

        var graph = MethodGraph.Create(methods, entry);

        Assert.Equal(1, graph.CallSiteCount("Caller"));
        Assert.Equal(1, graph.CallSiteCount("Second"));
        Assert.Contains("First", graph.RecursiveMethods);
        Assert.Contains("Second", graph.RecursiveMethods);
        Assert.DoesNotContain("Caller", graph.RecursiveMethods);
        Assert.Equal(
            new[] { "Caller", "First", "Second" },
            graph.ConnectedClosure(["First"]).Order(StringComparer.Ordinal));
    }

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
    public void CSharpFrontendBindsAwaitAndAsyncMethodMetadataIntoIr()
    {
        const string source = """
            int marker = 1;
            async System.Threading.Tasks.Task<int> LocalAsync() =>
                await AsyncWorker.DeclaredAsync();

            static class AsyncWorker
            {
                public static async System.Threading.Tasks.Task<int> DeclaredAsync() =>
                    await System.Threading.Tasks.Task.FromResult(42);
            }
            """;
        var compiler = new SharpSqlCompiler();

        var result = compiler.Transpile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var program = Assert.IsType<IrProgram>(compiler.BoundProgram);
        var local = Assert.Single(program.Methods, method => method.Name == "LocalAsync");
        var declared = Assert.Single(program.Methods, method => method.Name == "DeclaredAsync");
        Assert.True(local.IsAsync);
        Assert.True(declared.IsAsync);
        Assert.Null(local.ContainingType);
        Assert.Equal("AsyncWorker", declared.ContainingType);

        var localAwait = Assert.IsType<IrAwaitExpression>(local.ExpressionBody);
        var localCall = Assert.IsType<IrInvocationExpression>(localAwait.Operand);
        Assert.Equal(IrType.Int, localAwait.Type);
        Assert.Equal(declared.Id, localCall.TargetMethodId);

        var declaredAwait = Assert.IsType<IrAwaitExpression>(declared.ExpressionBody);
        Assert.IsType<IrInvocationExpression>(declaredAwait.Operand);
        Assert.Equal(IrType.Int, declaredAwait.Type);

        var graph = MethodGraph.Create(program.Methods, program.EntryPoint);
        Assert.Contains(declared.Id, graph.Callees(local.Id));
        Assert.True(local.Behavior.Effects.HasFlag(MethodEffects.InvokesUnknown));
        Assert.True(local.Behavior.Effects.HasFlag(MethodEffects.MayThrow));
    }

    [Fact]
    public void CSharpFrontendBindsMethodGroupIdentityWithoutBackendSemanticLookup()
    {
        const string source = """
            var values = new List<int> { 1 };
            var tasks = values.Select(Work).ToList();
            await Task.WhenAll(tasks);

            async Task<int> Work(int value)
            {
                await Task.Delay(0);
                return value;
            }
            """;
        var compiler = new SharpSqlCompiler();

        compiler.Transpile(
            source,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.ServiceBroker });

        var program = Assert.IsType<IrProgram>(compiler.BoundProgram);
        var work = Assert.Single(program.Methods, method => method.Name == "Work");
        var tasks = Assert.IsType<ProceduralDeclarationStatement>(program.EntryPoint.Statements[1]);
        var toList = Assert.IsType<IrInvocationExpression>(Assert.Single(tasks.Declaration.Variables).Initializer);
        var select = Assert.IsType<IrInvocationExpression>(Assert.IsType<IrMemberExpression>(toList.Target).Receiver);
        var methodGroup = Assert.IsType<IrVariableExpression>(Assert.Single(select.Arguments));

        Assert.Equal(work.Id, methodGroup.Symbol.ReferencedMethodId);
    }

    [Fact]
    public void CSharpFrontendBindsInferredLambdaParameterTypes()
    {
        const string source = """
            var tasks = new List<Task<Person>>();
            var results = tasks.Select(task => task.Result);
            var ordered = results.OrderBy(person => person.Age);
            record Person(int Age);
            """;
        var compiler = new SharpSqlCompiler();

        compiler.Transpile(source);

        var program = Assert.IsType<IrProgram>(compiler.BoundProgram);
        var declaration = Assert.IsType<ProceduralDeclarationStatement>(program.EntryPoint.Statements[2]);
        var orderBy = Assert.IsType<IrInvocationExpression>(Assert.Single(declaration.Declaration.Variables).Initializer);
        var lambda = Assert.IsType<IrLambdaExpression>(Assert.Single(orderBy.Arguments));
        var parameter = Assert.Single(lambda.Parameters);
        var member = Assert.IsType<IrMemberExpression>(lambda.ExpressionBody);

        Assert.Equal("Person", parameter.Type.Name);
        Assert.Equal(parameter, Assert.IsType<IrVariableExpression>(member.Receiver).Symbol);
    }

    [Fact]
    public void CSharpFrontendBindsMethodFlowSummariesIntoIr()
    {
        const string source = """
            int marker = 1;
            int Choose(bool choose)
            {
                if (choose)
                    return 1;
                return 2;
            }
            int Incomplete(bool choose)
            {
                if (choose)
                    return 1;
            }
            """;
        var compiler = new SharpSqlCompiler();

        var result = compiler.Transpile(source);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CS0161");
        var methods = Assert.IsType<IrProgram>(compiler.BoundProgram).Methods.ToDictionary(method => method.Name);
        Assert.Equal(new MethodFlowSummary(EndPointIsReachable: false, StatementCount: 3), methods["Choose"].Flow);
        Assert.Equal(new MethodFlowSummary(EndPointIsReachable: true, StatementCount: 2), methods["Incomplete"].Flow);
    }

    [Fact]
    public void CSharpFrontendBindsSourceFilePathsIntoIr()
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            "int value = 1;",
            path: "BoundInput.cs",
            cancellationToken: TestContext.Current.CancellationToken);
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "BoundInput",
            [tree],
            options: new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                Microsoft.CodeAnalysis.OutputKind.ConsoleApplication));
        var compiler = new SharpSqlCompiler();

        var result = compiler.Transpile(compilation);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var program = Assert.IsType<IrProgram>(compiler.BoundProgram);
        Assert.Equal("BoundInput.cs", program.EntryPoint.Source.FilePath);
        var declaration = Assert.IsType<ProceduralDeclarationStatement>(Assert.Single(program.EntryPoint.Statements));
        Assert.Equal("BoundInput.cs", declaration.Source.FilePath);
    }

    [Fact]
    public void IrDiagnosticsUseTheBoundSourcePathWithoutRoslyn()
    {
        var source = new IrSource(
            new IrSourceSpan(Start: 12, Length: 4, Line: 3, Column: 7),
            "BoundInput.cs",
            [],
            [],
            []);
        var program = new IrProgram(
            [],
            new ProceduralBlock(source, [new ProceduralUnsupported(source, "test")]),
            []);

        var result = new SharpSqlCompiler().Transpile(program);

        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "SS4003");
        Assert.Equal("BoundInput.cs", diagnostic.FilePath);
        Assert.Equal(3, diagnostic.Line);
        Assert.Equal(7, diagnostic.Column);
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
    public void HeapBackendDiagnosesArrayRankFromIrWithoutRoslyn()
    {
        var source = IrSource.None;
        var arrayType = new IrType("int[,]", IsReference: true);
        var facts = new ExpressionFacts(arrayType, ScalarNullability.NonNull, false, null);
        var array = new IrArrayCreationExpression(source, facts, IrType.Int, null, []) { Rank = 2 };
        var symbol = new IrSymbol(new IrSymbolId(1), "values", arrayType);
        var program = new IrProgram(
            [],
            new ProceduralBlock(
                source,
                [new ProceduralDeclarationStatement(
                    source,
                    new ProceduralDeclaration(source, [new ProceduralVariable(source, symbol, array)]))]),
            []);

        var result = new SharpSqlCompiler().Transpile(program);

        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "SS6301");
        Assert.Equal("Only one-dimensional arrays are supported.", diagnostic.Message);
    }

    [Fact]
    public void SqlBackendLowersUserMethodsConstructedWithoutRoslyn()
    {
        var source = IrSource.None;
        var intFacts = new ExpressionFacts(IrType.Int, ScalarNullability.NonNull, false, null);
        var boolFacts = new ExpressionFacts(IrType.Bool, ScalarNullability.NonNull, false, null);
        var value = new IrSymbol(new IrSymbolId(1), "value", IrType.Int);
        var valueExpression = new IrVariableExpression(source, intFacts, value);
        var zero = new IrConstantExpression(source, intFacts, 0, "0");
        var square = new MethodDefinition(
            "Square",
            IrType.Int,
            [new ParameterDefinition(value)],
            null,
            new IrBinaryExpression(
                source,
                intFacts,
                IrBinaryOperator.Multiply,
                valueExpression,
                valueExpression),
            source);
        var choose = new MethodDefinition(
            "ChoosePositive",
            IrType.Int,
            [new ParameterDefinition(value)],
            new ProceduralBlock(
                source,
                [new ProceduralIf(
                    source,
                    new IrBinaryExpression(
                        source,
                        boolFacts,
                        IrBinaryOperator.GreaterThan,
                        valueExpression,
                        zero),
                    new ProceduralReturn(source, valueExpression),
                    new ProceduralReturn(source, zero))]),
            null,
            source)
        {
            Flow = new MethodFlowSummary(EndPointIsReachable: false, StatementCount: 3)
        };
        IrInvocationExpression Call(string name, int argument) => new(
            source,
            intFacts,
            new IrVariableExpression(
                source,
                new ExpressionFacts(IrType.Unknown, ScalarNullability.Unknown, false, null),
                new IrSymbol(IrSymbolId.None, name, IrType.Unknown)),
            [new IrConstantExpression(source, intFacts, argument, argument.ToString())]);
        var squared = new IrSymbol(new IrSymbolId(2), "squared", IrType.Int);
        var chosen = new IrSymbol(new IrSymbolId(3), "chosen", IrType.Int);
        var program = new IrProgram(
            [square, choose],
            new ProceduralBlock(
                source,
                [
                    new ProceduralDeclarationStatement(
                        source,
                        new ProceduralDeclaration(
                            source,
                            [new ProceduralVariable(source, squared, Call("Square", 3))])),
                    new ProceduralDeclarationStatement(
                        source,
                        new ProceduralDeclaration(
                            source,
                            [new ProceduralVariable(source, chosen, Call("ChoosePositive", -2))]))
                ]),
            []);

        var result = new SharpSqlCompiler().Transpile(program);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("DECLARE @squared INT = 3 * 3;", result.Sql);
        Assert.Contains("DECLARE @_choosepositive_1_value INT = -2;", result.Sql);
        Assert.Contains("DECLARE @chosen INT;", result.Sql);
        Assert.Contains("SET @chosen = 0;", result.Sql);
    }

    [Fact]
    public void SqlBackendLowersRecursiveMethodsConstructedWithoutRoslyn()
    {
        var source = IrSource.None;
        var intFacts = new ExpressionFacts(IrType.Int, ScalarNullability.NonNull, false, null);
        var boolFacts = new ExpressionFacts(IrType.Bool, ScalarNullability.NonNull, false, null);
        var value = new IrSymbol(new IrSymbolId(1), "value", IrType.Int);
        var valueExpression = new IrVariableExpression(source, intFacts, value);
        IrConstantExpression Int(int number) => new(source, intFacts, number, number.ToString());
        IrInvocationExpression Call(IrExpression argument) => new(
            source,
            intFacts,
            new IrVariableExpression(
                source,
                new ExpressionFacts(IrType.Unknown, ScalarNullability.Unknown, false, null),
                new IrSymbol(IrSymbolId.None, "CountDown", IrType.Unknown)),
            [argument]);
        var method = new MethodDefinition(
            "CountDown",
            IrType.Int,
            [new ParameterDefinition(value)],
            new ProceduralBlock(
                source,
                [
                    new ProceduralIf(
                        source,
                        new IrBinaryExpression(
                            source,
                            boolFacts,
                            IrBinaryOperator.LessThanOrEqual,
                            valueExpression,
                            Int(0)),
                        new ProceduralReturn(source, Int(0)),
                        null),
                    new ProceduralReturn(
                        source,
                        Call(new IrBinaryExpression(
                            source,
                            intFacts,
                            IrBinaryOperator.Subtract,
                            valueExpression,
                            Int(1))))
                ]),
            null,
            source)
        {
            Flow = new MethodFlowSummary(EndPointIsReachable: false, StatementCount: 4)
        };
        var resultSymbol = new IrSymbol(new IrSymbolId(2), "result", IrType.Int);
        var program = new IrProgram(
            [method],
            new ProceduralBlock(
                source,
                [new ProceduralDeclarationStatement(
                    source,
                    new ProceduralDeclaration(
                        source,
                        [new ProceduralVariable(source, resultSymbol, Call(Int(2)))]))]),
            []);

        var result = new SharpSqlCompiler().Transpile(program);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("CREATE TABLE #__sharpsql_stack", result.Sql);
        Assert.Contains("__sharpsql_vm_CountDown_entry", result.Sql);
        Assert.Contains("DECLARE @result INT;", result.Sql);
        Assert.Contains("DROP TABLE IF EXISTS #__sharpsql_stack;", result.Sql);
    }

    [Fact]
    public void SqlBackendLowersManagedArraysConstructedWithoutRoslyn()
    {
        var source = IrSource.None;
        var intFacts = new ExpressionFacts(IrType.Int, ScalarNullability.NonNull, false, null);
        var arrayType = new IrType("int[]", IsReference: true);
        var arrayFacts = new ExpressionFacts(arrayType, ScalarNullability.NonNull, false, null);
        var values = new IrSymbol(new IrSymbolId(1), "values", arrayType);
        var selected = new IrSymbol(new IrSymbolId(2), "selected", IrType.Int);
        var length = new IrSymbol(new IrSymbolId(3), "length", IrType.Int);
        var valuesExpression = new IrVariableExpression(source, arrayFacts, values);
        IrConstantExpression Int(int value) => new(source, intFacts, value, value.ToString());
        var program = new IrProgram(
            [],
            new ProceduralBlock(
                source,
                [
                    new ProceduralDeclarationStatement(
                        source,
                        new ProceduralDeclaration(
                            source,
                            [new ProceduralVariable(
                                source,
                                values,
                                new IrArrayCreationExpression(
                                    source,
                                    arrayFacts,
                                    IrType.Int,
                                    null,
                                    [Int(4), Int(9)]))])),
                    new ProceduralDeclarationStatement(
                        source,
                        new ProceduralDeclaration(
                            source,
                            [new ProceduralVariable(
                                source,
                                selected,
                                new IrElementExpression(source, intFacts, valuesExpression, [Int(1)]))])),
                    new ProceduralDeclarationStatement(
                        source,
                        new ProceduralDeclaration(
                            source,
                            [new ProceduralVariable(
                                source,
                                length,
                                new IrMemberExpression(source, intFacts, valuesExpression, "Length"))]))
                ]),
            []);

        var result = new SharpSqlCompiler().Transpile(program);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("CREATE TABLE #__sharpsql_objects", result.Sql);
        Assert.Contains("CREATE TABLE #__sharpsql_indexed_items", result.Sql);
        Assert.Contains("IF 1 < 0 OR 1 >= (SELECT __count", result.Sql);
        Assert.Contains("SET @selected = (SELECT CONVERT(INT, __value)", result.Sql);
        Assert.Contains("DECLARE @length INT = (SELECT __count", result.Sql);
        Assert.Contains("DROP TABLE IF EXISTS #__sharpsql_objects;", result.Sql);
    }

    [Fact]
    public void SqlBackendLowersByteArraysConstructedWithoutRoslyn()
    {
        var source = IrSource.None;
        var byteType = new IrType("byte");
        var byteFacts = new ExpressionFacts(byteType, ScalarNullability.NonNull, false, null);
        var intFacts = new ExpressionFacts(IrType.Int, ScalarNullability.NonNull, false, null);
        var arrayType = new IrType("byte[]", IsReference: true);
        var arrayFacts = new ExpressionFacts(arrayType, ScalarNullability.NonNull, false, null);
        var values = new IrSymbol(new IrSymbolId(1), "values", arrayType);
        var selected = new IrSymbol(new IrSymbolId(2), "selected", byteType);
        var length = new IrSymbol(new IrSymbolId(3), "length", IrType.Int);
        var valuesExpression = new IrVariableExpression(source, arrayFacts, values);
        IrConstantExpression Byte(int value) => new(source, byteFacts, (byte)value, value.ToString());
        IrConstantExpression Int(int value) => new(source, intFacts, value, value.ToString());
        var program = new IrProgram(
            [],
            new ProceduralBlock(
                source,
                [
                    new ProceduralDeclarationStatement(
                        source,
                        new ProceduralDeclaration(
                            source,
                            [new ProceduralVariable(
                                source,
                                values,
                                new IrArrayCreationExpression(
                                    source,
                                    arrayFacts,
                                    byteType,
                                    null,
                                    [Byte(4), Byte(9)]))])),
                    new ProceduralDeclarationStatement(
                        source,
                        new ProceduralDeclaration(
                            source,
                            [new ProceduralVariable(
                                source,
                                selected,
                                new IrElementExpression(source, byteFacts, valuesExpression, [Int(1)]))])),
                    new ProceduralDeclarationStatement(
                        source,
                        new ProceduralDeclaration(
                            source,
                            [new ProceduralVariable(
                                source,
                                length,
                                new IrMemberExpression(source, intFacts, valuesExpression, "Length"))]))
                ]),
            []);

        var result = new SharpSqlCompiler().Transpile(program);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("CONVERT(VARBINARY(MAX), CONVERT(BINARY(1), 4))", result.Sql, StringComparison.Ordinal);
        Assert.Contains("SELECT __binary_value FROM #__sharpsql_indexed_items", result.Sql, StringComparison.Ordinal);
        Assert.Contains("__owner_id = @values", result.Sql, StringComparison.Ordinal);
        Assert.Contains("DECLARE @length INT = (SELECT __count FROM #__sharpsql_objects", result.Sql, StringComparison.Ordinal);
        Assert.Contains("#__sharpsql_objects", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlBackendLowersRuntimeMemberReceiversConstructedWithoutRoslyn()
    {
        var source = IrSource.None;
        var intFacts = new ExpressionFacts(IrType.Int, ScalarNullability.NonNull, false, null);
        var listType = new IrType("List<int>", IsReference: true);
        var listFacts = new ExpressionFacts(listType, ScalarNullability.NonNull, false, null);
        var count = new IrSymbol(new IrSymbolId(1), "count", IrType.Int);
        IrConstantExpression Int(int value) => new(source, intFacts, value, value.ToString());
        var list = new IrObjectCreationExpression(
            source,
            listFacts,
            listType,
            [],
            [Int(4), Int(9)]);
        var program = new IrProgram(
            [],
            new ProceduralBlock(
                source,
                [new ProceduralDeclarationStatement(
                    source,
                    new ProceduralDeclaration(
                        source,
                        [new ProceduralVariable(
                            source,
                            count,
                            new IrMemberExpression(source, intFacts, list, "Count"))]))]),
            []);

        var result = new SharpSqlCompiler().Transpile(program);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("SET @count = (SELECT __count FROM #__sharpsql_objects", result.Sql);
    }

    [Fact]
    public void SqlBackendLowersManagedObjectsConstructedWithoutRoslyn()
    {
        var source = IrSource.None;
        var intFacts = new ExpressionFacts(IrType.Int, ScalarNullability.NonNull, false, null);
        var personType = new IrType("Person", IsReference: true);
        var personFacts = new ExpressionFacts(personType, ScalarNullability.NonNull, false, null);
        var person = new IrSymbol(new IrSymbolId(1), "person", personType);
        var age = new IrSymbol(new IrSymbolId(2), "age", IrType.Int);
        var personExpression = new IrVariableExpression(source, personFacts, person);
        var ageMember = new IrMemberExpression(source, intFacts, personExpression, "Age");
        var program = new IrProgram(
            [],
            new ProceduralBlock(
                source,
                [
                    new ProceduralDeclarationStatement(
                        source,
                        new ProceduralDeclaration(
                            source,
                            [new ProceduralVariable(
                                source,
                                person,
                                new IrObjectCreationExpression(
                                    source,
                                    personFacts,
                                    personType,
                                    [
                                        new IrConstantExpression(
                                            source,
                                            new ExpressionFacts(IrType.String, ScalarNullability.NonNull, true, "Ada"),
                                            "Ada",
                                            "\"Ada\""),
                                        new IrConstantExpression(source, intFacts, 36, "36")
                                    ],
                                    []))])),
                    new ProceduralExpressionStatement(
                        source,
                        new IrAssignmentExpression(
                            source,
                            intFacts,
                            IrAssignmentOperator.Add,
                            ageMember,
                            new IrConstantExpression(source, intFacts, 1, "1"))),
                    new ProceduralDeclarationStatement(
                        source,
                        new ProceduralDeclaration(
                            source,
                            [new ProceduralVariable(source, age, ageMember)]))
                ]),
            [])
        {
            HeapTypes =
            [
                new IrHeapTypeDefinition(
                    "Person",
                    IsValueType: false,
                    IsRecord: false,
                    [
                        new IrHeapFieldDefinition("Name", IrType.String, source),
                        new IrHeapFieldDefinition("Age", IrType.Int, source)
                    ],
                    [new IrHeapConstructorDefinition(["Name", "Age"])],
                    source)
            ]
        };

        var result = new SharpSqlCompiler().Transpile(program);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("CREATE TABLE #__sharpsql_type_1", result.Sql);
        Assert.Contains("INSERT INTO #__sharpsql_type_1 (__object_id, [Name], [Age])", result.Sql);
        Assert.Contains("UPDATE #__sharpsql_type_1 SET [Age] =", result.Sql);
        Assert.Contains("DECLARE @age INT = (SELECT [Age]", result.Sql);
    }

    [Fact]
    public void SqlBackendLowersManagedCollectionsConstructedWithoutRoslyn()
    {
        var source = IrSource.None;
        var intFacts = new ExpressionFacts(IrType.Int, ScalarNullability.NonNull, false, null);
        var boolFacts = new ExpressionFacts(IrType.Bool, ScalarNullability.NonNull, false, null);
        var voidFacts = new ExpressionFacts(IrType.Void, ScalarNullability.NonNull, false, null);
        var stringFacts = new ExpressionFacts(IrType.String, ScalarNullability.NonNull, true, null);
        var listType = new IrType("List<int>", IsReference: true);
        var dictionaryType = new IrType("Dictionary<string,int>", IsReference: true);
        var listFacts = new ExpressionFacts(listType, ScalarNullability.NonNull, false, null);
        var dictionaryFacts = new ExpressionFacts(dictionaryType, ScalarNullability.NonNull, false, null);
        var list = new IrSymbol(new IrSymbolId(1), "values", listType);
        var dictionary = new IrSymbol(new IrSymbolId(2), "lookup", dictionaryType);
        var selected = new IrSymbol(new IrSymbolId(3), "selected", IrType.Int);
        var contains = new IrSymbol(new IrSymbolId(4), "contains", IrType.Bool);
        var listExpression = new IrVariableExpression(source, listFacts, list);
        var dictionaryExpression = new IrVariableExpression(source, dictionaryFacts, dictionary);
        IrConstantExpression Int(int value) => new(source, intFacts, value, value.ToString());
        IrConstantExpression Text(string value) => new(source, stringFacts, value, $"\"{value}\"");
        IrInvocationExpression Invoke(
            IrExpression receiver,
            string method,
            ExpressionFacts facts,
            params IrExpression[] arguments) => new(
                source,
                facts,
                new IrMemberExpression(
                    source,
                    new ExpressionFacts(IrType.Unknown, ScalarNullability.Unknown, false, null),
                    receiver,
                    method),
                arguments);
        var program = new IrProgram(
            [],
            new ProceduralBlock(
                source,
                [
                    new ProceduralDeclarationStatement(
                        source,
                        new ProceduralDeclaration(
                            source,
                            [new ProceduralVariable(
                                source,
                                list,
                                new IrObjectCreationExpression(
                                    source,
                                    listFacts,
                                    listType,
                                    [],
                                    [Int(2), Int(3)]))])),
                    new ProceduralExpressionStatement(source, Invoke(listExpression, "Add", voidFacts, Int(5))),
                    new ProceduralExpressionStatement(
                        source,
                        new IrAssignmentExpression(
                            source,
                            intFacts,
                            IrAssignmentOperator.Assign,
                            new IrElementExpression(source, intFacts, listExpression, [Int(0)]),
                            Int(7))),
                    new ProceduralDeclarationStatement(
                        source,
                        new ProceduralDeclaration(
                            source,
                            [new ProceduralVariable(
                                source,
                                dictionary,
                                new IrObjectCreationExpression(
                                    source,
                                    dictionaryFacts,
                                    dictionaryType,
                                    [],
                                    []))])),
                    new ProceduralExpressionStatement(
                        source,
                        Invoke(dictionaryExpression, "Add", voidFacts, Text("seven"), Int(7))),
                    new ProceduralExpressionStatement(
                        source,
                        new IrAssignmentExpression(
                            source,
                            intFacts,
                            IrAssignmentOperator.Assign,
                            new IrElementExpression(source, intFacts, dictionaryExpression, [Text("nine")]),
                            Int(9))),
                    new ProceduralDeclarationStatement(
                        source,
                        new ProceduralDeclaration(
                            source,
                            [new ProceduralVariable(
                                source,
                                selected,
                                new IrElementExpression(source, intFacts, dictionaryExpression, [Text("seven")]))])),
                    new ProceduralDeclarationStatement(
                        source,
                        new ProceduralDeclaration(
                            source,
                            [new ProceduralVariable(
                                source,
                                contains,
                                Invoke(dictionaryExpression, "ContainsKey", boolFacts, Text("nine")))]))
                ]),
            []);

        var result = new SharpSqlCompiler().Transpile(program);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("CREATE TABLE #__sharpsql_indexed_items", result.Sql);
        Assert.Contains("CREATE TABLE #__sharpsql_dictionary_entries", result.Sql);
        Assert.Contains("UPDATE #__sharpsql_indexed_items SET __value", result.Sql);
        Assert.Contains("Duplicate dictionary key", result.Sql);
        Assert.Contains("DECLARE @selected INT;", result.Sql);
        Assert.Contains("DECLARE @contains BIT = CASE WHEN EXISTS", result.Sql);
    }

    [Fact]
    public void SqlBackendExecutesConstructorBodiesWithoutRoslyn()
    {
        var source = IrSource.None;
        var intFacts = Facts(IrType.Int);
        var boxType = new IrType("Box", IsReference: true);
        var boxFacts = Facts(boxType);
        var constructorId = new IrConstructorId("Box::.ctor(int)");
        var parameter = new IrSymbol(new IrSymbolId(1), "value", IrType.Int);
        var @this = new IrSymbol(new IrSymbolId(2), "this", boxType);
        var box = new IrSymbol(new IrSymbolId(3), "box", boxType);
        var parameterExpression = new IrVariableExpression(source, intFacts, parameter);
        var field = new IrMemberExpression(
            source,
            intFacts,
            new IrThisExpression(source, boxFacts, @this),
            "Value");
        var constructor = new IrHeapConstructorDefinition(["Value"])
        {
            Id = constructorId,
            Parameters = [new ParameterDefinition(parameter)],
            IsFieldAssignmentOnly = false,
            Body = new ProceduralBlock(source,
            [
                new ProceduralIf(
                    source,
                    new IrBinaryExpression(
                        source,
                        Facts(IrType.Bool),
                        IrBinaryOperator.LessThan,
                        parameterExpression,
                        Int(0)),
                    new ProceduralExpressionStatement(
                        source,
                        new IrAssignmentExpression(
                            source,
                            intFacts,
                            IrAssignmentOperator.Assign,
                            parameterExpression,
                            Int(0))),
                    null),
                new ProceduralExpressionStatement(
                    source,
                    new IrAssignmentExpression(
                        source,
                        intFacts,
                        IrAssignmentOperator.Assign,
                        field,
                        new IrBinaryExpression(
                            source,
                            intFacts,
                            IrBinaryOperator.Add,
                            parameterExpression,
                            Int(1))))
            ])
        };
        var creation = new IrObjectCreationExpression(source, boxFacts, boxType, [Int(-2)], [])
        {
            ConstructorId = constructorId
        };
        var program = new IrProgram(
            [],
            new ProceduralBlock(
                source,
                [new ProceduralDeclarationStatement(
                    source,
                    new ProceduralDeclaration(
                        source,
                        [new ProceduralVariable(source, box, creation)]))]),
            [])
        {
            HeapTypes =
            [
                new IrHeapTypeDefinition(
                    "Box",
                    IsValueType: false,
                    IsRecord: false,
                    [new IrHeapFieldDefinition("Value", IrType.Int, source)],
                    [constructor],
                    source)
            ]
        };

        var result = new SharpSqlCompiler().Transpile(program);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("DECLARE @_ctor_Box_value INT", result.Sql);
        Assert.Contains("IF @_ctor_Box_value < 0", result.Sql);
        Assert.Contains("UPDATE #__sharpsql_type_1 SET [Value]", result.Sql);

        IrConstantExpression Int(int value) => new(
            source,
            new ExpressionFacts(IrType.Int, ScalarNullability.NonNull, true, value),
            value,
            value.ToString());
    }

    [Fact]
    public void SqlBackendExecutesInheritedLayoutAndBaseConstructorsWithoutRoslyn()
    {
        var source = IrSource.None;
        var derivedType = new IrType("Derived", IsReference: true);
        var baseConstructorId = new IrConstructorId("Base::.ctor(int)");
        var derivedConstructorId = new IrConstructorId("Derived::.ctor(int)");
        var baseConstructor = new IrHeapConstructorDefinition(["BaseValue"])
        {
            Id = baseConstructorId
        };
        var derivedConstructor = new IrHeapConstructorDefinition(["DerivedValue"])
        {
            Id = derivedConstructorId,
            InitializerKind = IrConstructorInitializerKind.Base,
            InitializerConstructorId = baseConstructorId,
            InitializerArguments = [Int(7)]
        };
        var creation = new IrObjectCreationExpression(
            source,
            Facts(derivedType),
            derivedType,
            [Int(9)],
            [])
        {
            ConstructorId = derivedConstructorId
        };
        var program = new IrProgram(
            [],
            new ProceduralBlock(
                source,
                [new ProceduralDeclarationStatement(
                    source,
                    new ProceduralDeclaration(
                        source,
                        [new ProceduralVariable(
                            source,
                            new IrSymbol(new IrSymbolId(1), "item", derivedType),
                            creation)]))]),
            [])
        {
            HeapTypes =
            [
                new IrHeapTypeDefinition(
                    "Base",
                    IsValueType: false,
                    IsRecord: false,
                    [new IrHeapFieldDefinition("BaseValue", IrType.Int, source)],
                    [baseConstructor],
                    source),
                new IrHeapTypeDefinition(
                    "Derived",
                    IsValueType: false,
                    IsRecord: false,
                    [new IrHeapFieldDefinition("DerivedValue", IrType.Int, source)],
                    [derivedConstructor],
                    source)
                {
                    BaseType = new IrType("Base", IsReference: true)
                }
            ]
        };

        var result = new SharpSqlCompiler().Transpile(program);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("INSERT INTO #__sharpsql_type_1", result.Sql);
        Assert.Contains("INSERT INTO #__sharpsql_type_2", result.Sql);
        Assert.Contains("SET [BaseValue] = 7", result.Sql);
        Assert.Contains("SET [DerivedValue] = 9", result.Sql);

        IrConstantExpression Int(int value) => new(
            source,
            new ExpressionFacts(IrType.Int, ScalarNullability.NonNull, true, value),
            value,
            value.ToString());
    }

    [Fact]
    public void SqlBackendDispatchesVirtualAndInterfaceCallsWithoutRoslyn()
    {
        var source = IrSource.None;
        var baseType = new IrType("Base", IsReference: true);
        var derivedType = new IrType("Derived", IsReference: true);
        var interfaceType = new IrType("IValue", IsReference: true);
        var baseMethodId = new IrMethodId("Base.Read");
        var derivedMethodId = new IrMethodId("Derived.Read");
        var interfaceMethodId = new IrMethodId("IValue.Read");
        var baseMethod = new MethodDefinition(
            "Read",
            IrType.Int,
            [new ParameterDefinition(new IrSymbol(new IrSymbolId(10), "this", baseType))],
            null,
            Int(1),
            source,
            ContainingType: "Base",
            IsInstance: true)
        {
            Id = baseMethodId,
            IsVirtual = true
        };
        var derivedMethod = new MethodDefinition(
            "Read",
            IrType.Int,
            [new ParameterDefinition(new IrSymbol(new IrSymbolId(11), "this", derivedType))],
            null,
            Int(2),
            source,
            ContainingType: "Derived",
            IsInstance: true)
        {
            Id = derivedMethodId,
            IsOverride = true,
            OverriddenMethodId = baseMethodId,
            ImplementedInterfaceMethodIds = [interfaceMethodId]
        };
        var interfaceMethod = new MethodDefinition(
            "Read",
            IrType.Int,
            [new ParameterDefinition(new IrSymbol(new IrSymbolId(12), "this", interfaceType))],
            null,
            null,
            source,
            ContainingType: "IValue",
            IsInstance: true)
        {
            Id = interfaceMethodId,
            IsAbstract = true
        };
        var item = new IrSymbol(new IrSymbolId(1), "item", derivedType);
        var asBase = new IrSymbol(new IrSymbolId(2), "asBase", baseType);
        var asInterface = new IrSymbol(new IrSymbolId(3), "asInterface", interfaceType);
        var virtualResult = new IrSymbol(new IrSymbolId(4), "virtualResult", IrType.Int);
        var interfaceResult = new IrSymbol(new IrSymbolId(5), "interfaceResult", IrType.Int);
        var program = new IrProgram(
            [baseMethod, derivedMethod, interfaceMethod],
            new ProceduralBlock(source,
            [
                Declare(item, new IrObjectCreationExpression(source, Facts(derivedType), derivedType, [], [])),
                Declare(asBase, Variable(item)),
                Declare(asInterface, Variable(item)),
                Declare(virtualResult, Call(Variable(asBase), baseMethodId, IrCallDispatch.Virtual)),
                Declare(interfaceResult, Call(Variable(asInterface), interfaceMethodId, IrCallDispatch.Interface))
            ]),
            [])
        {
            HeapTypes =
            [
                new IrHeapTypeDefinition("Base", false, false, [], [], source),
                new IrHeapTypeDefinition("Derived", false, false, [], [], source)
                {
                    BaseType = baseType
                }
            ]
        };

        var result = new SharpSqlCompiler().Transpile(program);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("CASE (SELECT __type_id", result.Sql);
        Assert.Contains("__sharpsql_function_dispatch", result.Sql);
        Assert.Contains("stack-machine body: Read", result.Sql);

        IrConstantExpression Int(int value) => new(
            source,
            new ExpressionFacts(IrType.Int, ScalarNullability.NonNull, true, value),
            value,
            value.ToString());
        IrVariableExpression Variable(IrSymbol symbol) => new(source, Facts(symbol.Type), symbol);
        IrInvocationExpression Call(IrExpression receiver, IrMethodId methodId, IrCallDispatch dispatch) =>
            new(source, Facts(IrType.Int), new IrMemberExpression(source, Facts(IrType.Int), receiver, "Read"), [])
            {
                TargetMethodId = methodId,
                Dispatch = dispatch
            };
        ProceduralDeclarationStatement Declare(IrSymbol symbol, IrExpression initializer) =>
            new(source, new ProceduralDeclaration(source, [new ProceduralVariable(source, symbol, initializer)]));
    }

    [Fact]
    public void SqlBackendLowersRandomConstructedWithoutRoslyn()
    {
        var source = IrSource.None;
        var intFacts = new ExpressionFacts(IrType.Int, ScalarNullability.NonNull, false, null);
        var randomType = new IrType("Random", IsReference: true);
        var randomFacts = new ExpressionFacts(randomType, ScalarNullability.NonNull, false, null);
        var random = new IrSymbol(new IrSymbolId(1), "random", randomType);
        var roll = new IrSymbol(new IrSymbolId(2), "roll", IrType.Int);
        var randomExpression = new IrVariableExpression(source, randomFacts, random);
        var next = new IrInvocationExpression(
            source,
            intFacts,
            new IrMemberExpression(source, intFacts, randomExpression, "Next"),
            [
                new IrConstantExpression(source, intFacts, 1, "1"),
                new IrConstantExpression(source, intFacts, 7, "7")
            ]);
        var program = new IrProgram(
            [],
            new ProceduralBlock(
                source,
                [
                    new ProceduralDeclarationStatement(
                        source,
                        new ProceduralDeclaration(
                            source,
                            [new ProceduralVariable(
                                source,
                                random,
                                new IrObjectCreationExpression(
                                    source,
                                    randomFacts,
                                    randomType,
                                    [new IrConstantExpression(source, intFacts, 123, "123")],
                                    []))])),
                    new ProceduralDeclarationStatement(
                        source,
                        new ProceduralDeclaration(
                            source,
                            [new ProceduralVariable(source, roll, next)]))
                ]),
            []);

        var result = new SharpSqlCompiler().Transpile(program);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("DECLARE @_random_seed INT = 123;", result.Sql);
        Assert.Contains("Random minimum must not exceed maximum", result.Sql);
        Assert.Contains("DECLARE @roll INT;", result.Sql);
    }

    [Fact]
    public void SqlBackendLowersLinqPlansConstructedWithoutRoslyn()
    {
        var source = IrSource.None;
        var intFacts = new ExpressionFacts(IrType.Int, ScalarNullability.NonNull, false, null);
        var boolFacts = new ExpressionFacts(IrType.Bool, ScalarNullability.NonNull, false, null);
        var listType = new IrType("List<int>", IsReference: true);
        var sequenceType = new IrType("IEnumerable<int>", IsReference: true);
        var arrayType = new IrType("int[]", IsReference: true);
        var listFacts = new ExpressionFacts(listType, ScalarNullability.NonNull, false, null);
        var sequenceFacts = new ExpressionFacts(sequenceType, ScalarNullability.NonNull, false, null);
        var arrayFacts = new ExpressionFacts(arrayType, ScalarNullability.NonNull, false, null);
        var values = new IrSymbol(new IrSymbolId(1), "values", listType);
        var query = new IrSymbol(new IrSymbolId(2), "query", sequenceType);
        var total = new IrSymbol(new IrSymbolId(3), "total", IrType.Int);
        var materialized = new IrSymbol(new IrSymbolId(4), "materialized", arrayType);
        var item = new IrSymbol(new IrSymbolId(5), "item", IrType.Int);
        var itemExpression = new IrVariableExpression(source, intFacts, item);
        var valuesExpression = new IrVariableExpression(source, listFacts, values);
        var whereLambda = new IrLambdaExpression(
            source,
            new ExpressionFacts(new IrType("Func<int,bool>", IsReference: true), ScalarNullability.NonNull, false, null),
            [item],
            new IrBinaryExpression(
                source,
                boolFacts,
                IrBinaryOperator.GreaterThan,
                itemExpression,
                new IrConstantExpression(source, intFacts, 1, "1")),
            null);
        var selectLambda = new IrLambdaExpression(
            source,
            new ExpressionFacts(new IrType("Func<int,int>", IsReference: true), ScalarNullability.NonNull, false, null),
            [item],
            new IrBinaryExpression(
                source,
                intFacts,
                IrBinaryOperator.Multiply,
                itemExpression,
                new IrConstantExpression(source, intFacts, 2, "2")),
            null);
        var where = new IrInvocationExpression(
            source,
            sequenceFacts,
            new IrMemberExpression(source, sequenceFacts, valuesExpression, "Where"),
            [whereLambda]);
        var select = new IrInvocationExpression(
            source,
            sequenceFacts,
            new IrMemberExpression(source, sequenceFacts, where, "Select"),
            [selectLambda]);
        var queryExpression = new IrVariableExpression(source, sequenceFacts, query);
        var sum = new IrInvocationExpression(
            source,
            intFacts,
            new IrMemberExpression(source, intFacts, queryExpression, "Sum"),
            []);
        var toArray = new IrInvocationExpression(
            source,
            arrayFacts,
            new IrMemberExpression(source, arrayFacts, queryExpression, "ToArray"),
            []);
        var program = new IrProgram(
            [],
            new ProceduralBlock(
                source,
                [
                    new ProceduralDeclarationStatement(source, new ProceduralDeclaration(source,
                        [new ProceduralVariable(source, values, new IrObjectCreationExpression(
                            source, listFacts, listType, [],
                            [
                                new IrConstantExpression(source, intFacts, 1, "1"),
                                new IrConstantExpression(source, intFacts, 2, "2"),
                                new IrConstantExpression(source, intFacts, 3, "3")
                            ]))])),
                    new ProceduralDeclarationStatement(source, new ProceduralDeclaration(source,
                        [new ProceduralVariable(source, query, select)])),
                    new ProceduralDeclarationStatement(source, new ProceduralDeclaration(source,
                        [new ProceduralVariable(source, total, sum)])),
                    new ProceduralDeclarationStatement(source, new ProceduralDeclaration(source,
                        [new ProceduralVariable(source, materialized, toArray)]))
                ]),
            []);

        var result = new SharpSqlCompiler().Transpile(program);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("DECLARE @query INT = @values;", result.Sql);
        Assert.Contains("DECLARE @total INT = COALESCE((SELECT SUM", result.Sql);
        Assert.Contains("_linq_materialized_count", result.Sql);
    }

    [Fact]
    public void SqlBackendLowersAdvancedLinqPlansConstructedWithoutRoslyn()
    {
        var source = IrSource.None;
        var intFacts = Facts(IrType.Int);
        var boolFacts = Facts(IrType.Bool);
        var listType = new IrType("List<int>", IsReference: true);
        var sequenceType = new IrType("IEnumerable<int>", IsReference: true);
        var groupingSequenceType = new IrType("IEnumerable<IGrouping<int,int>>", IsReference: true);
        var values = new IrSymbol(new IrSymbolId(1), "values", listType);
        var item = new IrSymbol(new IrSymbolId(2), "item", IrType.Int);
        var inner = new IrSymbol(new IrSymbolId(3), "inner", IrType.Int);
        var valuesExpression = new IrVariableExpression(source, Facts(listType), values);
        var itemExpression = new IrVariableExpression(source, intFacts, item);
        var innerExpression = new IrVariableExpression(source, intFacts, inner);
        IrLambdaExpression Lambda(IrSymbol parameter, IrExpression body) => new(
            source,
            Facts(new IrType($"Func<int,{body.Type.Name}>", IsReference: true)),
            [parameter],
            body,
            null);
        var query = new IrQueryExpression(
            source,
            Facts(sequenceType),
            item,
            valuesExpression,
            [
                new IrWhereClause(source, new IrBinaryExpression(
                    source, boolFacts, IrBinaryOperator.GreaterThan, itemExpression, Int(1))),
                new IrOrderClause(source, itemExpression, Descending: true, IsThenBy: false),
                new IrSelectClause(source, new IrBinaryExpression(
                    source, intFacts, IrBinaryOperator.Multiply, itemExpression, Int(2)))
            ]);
        var parity = new IrBinaryExpression(source, intFacts, IrBinaryOperator.Remainder, itemExpression, Int(2));
        var grouped = Invoke(valuesExpression, "GroupBy", groupingSequenceType, Lambda(item, parity));
        var joined = Invoke(
            valuesExpression,
            "Join",
            sequenceType,
            valuesExpression,
            Lambda(item, itemExpression),
            Lambda(inner, innerExpression),
            new IrLambdaExpression(
                source,
                Facts(new IrType("Func<int,int,int>", IsReference: true)),
                [item, inner],
                new IrBinaryExpression(source, intFacts, IrBinaryOperator.Add, itemExpression, innerExpression),
                null));
        var queryCount = new IrSymbol(new IrSymbolId(4), "queryCount", IrType.Int);
        var groupCount = new IrSymbol(new IrSymbolId(5), "groupCount", IrType.Int);
        var joinTotal = new IrSymbol(new IrSymbolId(6), "joinTotal", IrType.Int);
        var first = new IrSymbol(new IrSymbolId(7), "first", IrType.Int);
        var program = new IrProgram(
            [],
            new ProceduralBlock(source,
            [
                Declare(values, new IrObjectCreationExpression(
                    source, Facts(listType), listType, [], [Int(1), Int(2), Int(3)])),
                Declare(queryCount, Invoke(query, "Count", IrType.Int)),
                Declare(groupCount, Invoke(grouped, "Count", IrType.Int)),
                Declare(joinTotal, Invoke(joined, "Sum", IrType.Int)),
                Declare(first, Invoke(query, "First", IrType.Int))
            ]),
            []);

        var result = new SharpSqlCompiler().Transpile(program);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("GROUP BY", result.Sql);
        Assert.Contains("INNER JOIN", result.Sql);
        Assert.Contains("SELECT SUM", result.Sql);
        Assert.Contains("THROW 51007, 'LINQ sequence contains no elements.'", result.Sql);
        Assert.Contains("ORDER BY", result.Sql);

        IrConstantExpression Int(int value) => new(source, intFacts, value, value.ToString());
        IrInvocationExpression Invoke(IrExpression receiver, string method, IrType resultType, params IrExpression[] arguments) =>
            new(source, Facts(resultType), new IrMemberExpression(source, Facts(resultType), receiver, method), arguments);
        ProceduralDeclarationStatement Declare(IrSymbol symbol, IrExpression initializer) =>
            new(source, new ProceduralDeclaration(source, [new ProceduralVariable(source, symbol, initializer)]));
    }

    [Fact]
    public void SqlBackendInlinesLinqHelpersAndDelegateFactoriesWithoutRoslyn()
    {
        var source = IrSource.None;
        var intFacts = Facts(IrType.Int);
        var listType = new IrType("List<int>", IsReference: true);
        var sequenceType = new IrType("IEnumerable<int>", IsReference: true);
        var delegateType = new IrType("Func<int,int>", IsReference: true);
        var sourceParameter = new IrSymbol(new IrSymbolId(10), "source", sequenceType);
        var selectorParameter = new IrSymbol(new IrSymbolId(11), "selector", delegateType);
        var offsetParameter = new IrSymbol(new IrSymbolId(12), "offset", IrType.Int);
        var lambdaItem = new IrSymbol(new IrSymbolId(13), "value", IrType.Int);
        var selectorFactory = new MethodDefinition(
            "AddOffset",
            delegateType,
            [new ParameterDefinition(offsetParameter)],
            null,
            new IrLambdaExpression(
                source,
                Facts(delegateType),
                [lambdaItem],
                new IrBinaryExpression(
                    source,
                    intFacts,
                    IrBinaryOperator.Add,
                    new IrVariableExpression(source, intFacts, lambdaItem),
                    new IrVariableExpression(source, intFacts, offsetParameter)),
                null),
            source);
        var apply = new MethodDefinition(
            "Apply",
            sequenceType,
            [new ParameterDefinition(sourceParameter), new ParameterDefinition(selectorParameter)],
            null,
            Invoke(
                new IrVariableExpression(source, Facts(sequenceType), sourceParameter),
                "Select",
                sequenceType,
                new IrVariableExpression(source, Facts(delegateType), selectorParameter)),
            source);
        var values = new IrSymbol(new IrSymbolId(1), "values", listType);
        var total = new IrSymbol(new IrSymbolId(2), "total", IrType.Int);
        var valuesExpression = new IrVariableExpression(source, Facts(listType), values);
        var factoryCall = Call("AddOffset", delegateType, Int(3));
        var helperCall = Call("Apply", sequenceType, valuesExpression, factoryCall);
        var program = new IrProgram(
            [selectorFactory, apply],
            new ProceduralBlock(source,
            [
                Declare(values, new IrObjectCreationExpression(
                    source, Facts(listType), listType, [], [Int(1), Int(2)])),
                Declare(total, Invoke(helperCall, "Sum", IrType.Int))
            ]),
            []);

        var result = new SharpSqlCompiler().Transpile(program);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("DECLARE @_linq_capture INT = 3;", result.Sql);
        Assert.Contains("SELECT SUM", result.Sql);
        Assert.Contains("+ @_linq_capture", result.Sql);

        IrConstantExpression Int(int value) => new(source, intFacts, value, value.ToString());
        IrInvocationExpression Call(string method, IrType resultType, params IrExpression[] arguments) =>
            new(source, Facts(resultType), new IrVariableExpression(
                source, Facts(IrType.Unknown), new IrSymbol(IrSymbolId.None, method, IrType.Unknown)), arguments);
        IrInvocationExpression Invoke(IrExpression receiver, string method, IrType resultType, params IrExpression[] arguments) =>
            new(source, Facts(resultType), new IrMemberExpression(source, Facts(resultType), receiver, method), arguments);
        ProceduralDeclarationStatement Declare(IrSymbol symbol, IrExpression initializer) =>
            new(source, new ProceduralDeclaration(source, [new ProceduralVariable(source, symbol, initializer)]));
    }

    [Fact]
    public void SqlBackendMaterializesRepeatSelectorsSequentiallyWithoutRoslyn()
    {
        var source = IrSource.None;
        var intFacts = Facts(IrType.Int);
        var boxType = new IrType("Box", IsReference: true);
        var sequenceType = new IrType("IEnumerable<Box>", IsReference: true);
        var listType = new IrType("List<Box>", IsReference: true);
        var enumerable = new IrVariableExpression(
            source,
            Facts(IrType.Unknown),
            new IrSymbol(IrSymbolId.None, "Enumerable", IrType.Unknown));
        var item = new IrSymbol(new IrSymbolId(1), "item", IrType.Int);
        var repeated = Invoke(enumerable, "Repeat", new IrType("IEnumerable<int>", IsReference: true), Int(4), Int(3));
        var selected = Invoke(
            repeated,
            "Select",
            sequenceType,
            new IrLambdaExpression(
                source,
                Facts(new IrType("Func<int,Box>", IsReference: true)),
                [item],
                new IrObjectCreationExpression(
                    source,
                    Facts(boxType),
                    boxType,
                    [new IrVariableExpression(source, intFacts, item)],
                    []),
                null));
        var boxes = new IrSymbol(new IrSymbolId(2), "boxes", listType);
        var materialized = Invoke(selected, "ToList", listType);
        var program = new IrProgram(
            [],
            new ProceduralBlock(source, [Declare(boxes, materialized)]),
            [])
        {
            HeapTypes =
            [
                new IrHeapTypeDefinition(
                    "Box",
                    IsValueType: false,
                    IsRecord: false,
                    [new IrHeapFieldDefinition("Value", IrType.Int, source)],
                    [new IrHeapConstructorDefinition(["Value"])],
                    source)
            ]
        };

        var result = new SharpSqlCompiler().Transpile(program);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("Enumerable.Repeat count must be non-negative.", result.Sql);
        Assert.Contains("WHILE @_repeat_index <", result.Sql);
        Assert.Contains("INSERT INTO #__sharpsql_indexed_items", result.Sql);

        IrConstantExpression Int(int value) => new(source, intFacts, value, value.ToString());
        IrInvocationExpression Invoke(IrExpression receiver, string method, IrType resultType, params IrExpression[] arguments) =>
            new(source, Facts(resultType), new IrMemberExpression(source, Facts(resultType), receiver, method), arguments);
        ProceduralDeclarationStatement Declare(IrSymbol symbol, IrExpression initializer) =>
            new(source, new ProceduralDeclaration(source, [new ProceduralVariable(source, symbol, initializer)]));
    }

    private static ExpressionFacts Facts(IrType type) =>
        new(type, type.IsReference ? ScalarNullability.MaybeNull : ScalarNullability.NonNull, false, null);

    [Fact]
    public void CSharpFrontendBindsTypeHierarchyAndConstructorBodiesIntoIr()
    {
        const string source = """
            int marker = 1;
            interface INamed { string Name { get; } }
            abstract class Entity
            {
                public int Id;
                protected Entity(int id) { Id = id; }
            }
            sealed class Person : Entity, INamed
            {
                public string Name { get; }
                public Person(int id, string name) : base(id) { Name = name; }
            }
            """;
        var compiler = new SharpSqlCompiler();

        var result = compiler.Transpile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var program = Assert.IsType<IrProgram>(compiler.BoundProgram);
        var entity = Assert.Single(program.HeapTypes, type => type.Name == "Entity");
        var person = Assert.Single(program.HeapTypes, type => type.Name == "Person");
        Assert.False(entity.Id.IsNone);
        Assert.False(person.Id.IsNone);
        Assert.Equal("Entity", person.BaseType?.Name);
        Assert.Contains(person.Interfaces, type => type.Name == "INamed");
        Assert.True(person.IsSealed);
        var field = Assert.Single(person.Fields, item => item.Name == "Name");
        Assert.Equal(IrMemberKind.Property, field.Kind);
        Assert.False(field.Id.IsNone);
        var constructor = Assert.Single(person.Constructors);
        Assert.False(constructor.Id.IsNone);
        Assert.Equal(new[] { "id", "name" }, constructor.Parameters.Select(parameter => parameter.Name));
        Assert.Equal(IrConstructorInitializerKind.Base, constructor.InitializerKind);
        Assert.False(constructor.InitializerConstructorId.IsNone);
        Assert.Single(constructor.InitializerArguments);
        var body = Assert.IsType<ProceduralBlock>(constructor.Body);
        var statement = Assert.IsType<ProceduralExpressionStatement>(Assert.Single(body.Statements));
        Assert.IsType<IrAssignmentExpression>(statement.Expression);
    }

    [Fact]
    public void CSharpFrontendBindsResolvedCallAndConstructionIdentities()
    {
        const string source = """
            int Twice(int value) => value * 2;
            var result = Twice(4);
            var box = new Box(result);
            class Box
            {
                public int Value;
                public Box(int value) { Value = value; }
            }
            """;
        var compiler = new SharpSqlCompiler();

        var result = compiler.Transpile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var program = Assert.IsType<IrProgram>(compiler.BoundProgram);
        var method = Assert.Single(program.Methods, candidate => candidate.Name == "Twice");
        Assert.False(method.Id.IsNone);
        var resultDeclaration = Assert.IsType<ProceduralDeclarationStatement>(program.EntryPoint.Statements[0]);
        var invocation = Assert.IsType<IrInvocationExpression>(Assert.Single(resultDeclaration.Declaration.Variables).Initializer);
        Assert.Equal(method.Id, invocation.TargetMethodId);
        Assert.Equal(IrCallDispatch.Direct, invocation.Dispatch);

        var type = Assert.Single(program.HeapTypes, candidate => candidate.Name == "Box");
        var constructor = Assert.Single(type.Constructors);
        var boxDeclaration = Assert.IsType<ProceduralDeclarationStatement>(program.EntryPoint.Statements[1]);
        var creation = Assert.IsType<IrObjectCreationExpression>(Assert.Single(boxDeclaration.Declaration.Variables).Initializer);
        Assert.Equal(constructor.Id, creation.ConstructorId);
        Assert.False(creation.ConstructorId.IsNone);
    }

    [Fact]
    public void ResolvedMethodCatalogSupportsOverloadsAndTracksRecursiveOverloadsSeparately()
    {
        const string source = """
            var number = Helpers.Pick(2);
            var text = Helpers.Pick("A");
            var first = Helpers.Recur(3);
            var second = Helpers.Recur(3, 4);
            var left = Left.Read();
            var right = Right.Read();
            static class Helpers
            {
                public static int Pick(int value) => value + 1;
                public static string Pick(string value) => value + "!";
                public static int Recur(int value)
                {
                    if (value == 0) return 0;
                    return Recur(value - 1) + 1;
                }
                public static int Recur(int value, int total)
                {
                    if (value == 0) return total;
                    return Recur(value - 1, total + 1);
                }
            }
            static class Left { public static int Read() => 10; }
            static class Right { public static int Read() => 20; }
            """;
        var compiler = new SharpSqlCompiler();

        var result = compiler.Transpile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var program = Assert.IsType<IrProgram>(compiler.BoundProgram);
        var picks = program.Methods.Where(method => method.Name == "Pick").ToArray();
        var recursive = program.Methods.Where(method => method.Name == "Recur").ToArray();
        var reads = program.Methods.Where(method => method.Name == "Read").ToArray();
        Assert.Equal(2, picks.Length);
        Assert.Equal(2, picks.Select(method => method.Id).Distinct().Count());
        Assert.Equal(2, recursive.Length);
        Assert.Equal(2, recursive.Select(method => method.Id).Distinct().Count());
        Assert.Equal(new[] { "Left", "Right" }, reads.Select(method => method.ContainingType).Order());
        Assert.Equal(2, reads.Select(method => method.Id).Distinct().Count());

        var calls = program.EntryPoint.Statements
            .OfType<ProceduralDeclarationStatement>()
            .Select(statement => Assert.IsType<IrInvocationExpression>(
                Assert.Single(statement.Declaration.Variables).Initializer))
            .ToArray();
        Assert.Equal(6, calls.Select(call => call.TargetMethodId).Distinct().Count());
        Assert.All(calls, call => Assert.False(call.TargetMethodId.IsNone));

        var graph = MethodGraph.Create(program.Methods, program.EntryPoint);
        Assert.All(recursive, method => Assert.Contains(method.Id, graph.RecursiveMethodIds));
        Assert.Contains("SharpSql stack-machine runtime", result.Sql);
    }

    [Fact]
    public void CSharpFrontendRetainsOverrideInterfaceAndDispatchMetadata()
    {
        const string source = """
            int marker = 1;
            int InterfaceRead(IValue value) => value.Read();
            int VirtualRead(Base value) => value.Read();
            interface IValue { int Read(); }
            abstract class Base : IValue { public abstract int Read(); }
            sealed class Derived : Base { public override int Read() => 42; }
            """;
        var compiler = new SharpSqlCompiler();

        var result = compiler.Transpile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var methods = Assert.IsType<IrProgram>(compiler.BoundProgram).Methods;
        var baseMethod = Assert.Single(methods, method => method.Name == "Read" && method.ContainingType == "Base");
        var derivedMethod = Assert.Single(methods, method => method.Name == "Read" && method.ContainingType == "Derived");
        Assert.True(baseMethod.IsAbstract);
        Assert.True(derivedMethod.IsOverride);
        Assert.Equal(baseMethod.Id, derivedMethod.OverriddenMethodId);
        Assert.NotEmpty(baseMethod.ImplementedInterfaceMethodIds);

        var interfaceCall = Assert.IsType<IrInvocationExpression>(
            Assert.Single(methods, method => method.Name == "InterfaceRead").PureExpression);
        var virtualCall = Assert.IsType<IrInvocationExpression>(
            Assert.Single(methods, method => method.Name == "VirtualRead").PureExpression);
        Assert.Equal(IrCallDispatch.Interface, interfaceCall.Dispatch);
        Assert.Equal(IrCallDispatch.Virtual, virtualCall.Dispatch);
        Assert.Equal(baseMethod.Id, virtualCall.TargetMethodId);
    }

    [Fact]
    public void CSharpFrontendRetainsConstructorControlFlowInIr()
    {
        const string source = """
            int marker = 1;
            class Counter
            {
                public int Value;
                public Counter(int value)
                {
                    if (value < 0)
                        value = 0;
                    Value = value;
                }
            }
            """;
        var compiler = new SharpSqlCompiler();

        var result = compiler.Transpile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var constructor = Assert.Single(
            Assert.IsType<IrProgram>(compiler.BoundProgram).HeapTypes,
            type => type.Name == "Counter").Constructors.Single();
        Assert.False(constructor.IsFieldAssignmentOnly);
        var body = Assert.IsType<ProceduralBlock>(constructor.Body);
        Assert.IsType<ProceduralIf>(body.Statements[0]);
        Assert.IsType<ProceduralExpressionStatement>(body.Statements[1]);
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

    [Fact]
    public void MethodBehaviorSummariesPropagateEffectsAliasesAndEscapes()
    {
        const string source = """
            Console.WriteLine("analysis");
            Box MutateAndReturn(Box value)
            {
                value.Value += 1;
                return value;
            }

            Box Forward(Box value) => MutateAndReturn(value);
            Box Choose(bool second, Box first, Box other)
            {
                Box result = first;
                if (second)
                    result = other;
                return result;
            }
            Box Fresh() => new Box();
            IEnumerable<int> Above(List<int> values, Box threshold) =>
                values.Where(value => value > threshold.Value);
            class Box { public int Value; }
            """;
        var compiler = new SharpSqlCompiler();

        var result = compiler.Transpile(source);

        var methods = Assert.IsType<IrProgram>(compiler.BoundProgram).Methods.ToDictionary(method => method.Name);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var mutation = methods["MutateAndReturn"].Behavior;
        Assert.True(mutation.Effects.HasFlag(MethodEffects.ReadsMutableState));
        Assert.True(mutation.Effects.HasFlag(MethodEffects.WritesMutableState));
        Assert.Contains(0, mutation.MutatedParameters);
        Assert.Contains(0, mutation.EscapingParameters);
        Assert.Contains(0, mutation.ReturnedParameters);

        var forwarded = methods["Forward"].Behavior;
        Assert.True(forwarded.Effects.HasFlag(MethodEffects.WritesMutableState));
        Assert.Contains(0, forwarded.MutatedParameters);
        Assert.Contains(0, forwarded.EscapingParameters);
        Assert.Contains(0, forwarded.ReturnedParameters);

        var choice = methods["Choose"].Behavior;
        Assert.Equal(new[] { 1, 2 }, choice.ReturnedParameters.Order());
        Assert.Equal(new[] { 1, 2 }, choice.EscapingParameters.Order());

        var fresh = methods["Fresh"].Behavior;
        Assert.True(fresh.Effects.HasFlag(MethodEffects.Allocates));
        Assert.True(fresh.ReturnsFreshReference);
        Assert.Empty(fresh.ReturnedParameters);

        var deferred = methods["Above"].Behavior;
        Assert.True(deferred.Effects.HasFlag(MethodEffects.ReadsMutableState));
        Assert.Equal(new[] { 0, 1 }, deferred.EscapingParameters.Order());
        Assert.True(deferred.ReturnsUnknownReference);
    }

    [Fact]
    public void RetainsDiscardedCallsThatMayThrowTransitively()
    {
        const string source = """
            int Divide(int divisor) => 10 / divisor;
            int Forward(int divisor) => Divide(divisor);
            Forward(0);
            """;
        var compiler = new SharpSqlCompiler();

        var result = compiler.Transpile(source);

        var forward = Assert.IsType<IrProgram>(compiler.BoundProgram).Methods.Single(method => method.Name == "Forward");
        Assert.True(
            result.Success,
            $"Effects: {forward.Behavior.Effects}{Environment.NewLine}" +
            string.Join(Environment.NewLine, result.Diagnostics) + Environment.NewLine + result.Sql);
        Assert.True(forward.Behavior.Effects.HasFlag(MethodEffects.MayThrow));
        Assert.Contains("DECLARE @_discarded INT = 10 / 0;", result.Sql);
    }
}
