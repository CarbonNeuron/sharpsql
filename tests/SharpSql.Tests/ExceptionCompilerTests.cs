using Xunit;

namespace SharpSql.Tests;

public sealed class ExceptionCompilerTests
{
    [Fact]
    public void BindsTryCatchThrowAndExceptionMetadataIntoProceduralIr()
    {
        const string source = """
            try
            {
                throw new ApplicationException("boom");
            }
            catch (ApplicationException exception)
            {
                Console.WriteLine(exception.Message);
            }
            """;
        var compiler = new SharpSqlCompiler();

        var result = compiler.Transpile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var program = Assert.IsType<IrProgram>(compiler.BoundProgram);
        var @try = Assert.IsType<ProceduralTry>(Assert.Single(program.EntryPoint.Statements));
        var @throw = Assert.IsType<ProceduralThrow>(Assert.Single(@try.Body.Statements));
        Assert.Equal("System.ApplicationException", @throw.ExceptionType?.MetadataName);
        Assert.IsType<IrObjectCreationExpression>(@throw.Expression);
        var @catch = Assert.Single(@try.Catches);
        Assert.Equal("System.ApplicationException", @catch.ExceptionType?.MetadataName);
        Assert.Equal("exception", @catch.Exception?.Name);
        Assert.IsType<ProceduralExpressionStatement>(Assert.Single(@catch.Body.Statements));
    }

    [Fact]
    public void LowersApplicationExceptionCatchMetadataAndRethrow()
    {
        const string source = """
            try
            {
                throw new ApplicationException($"boom-{1 + 1}");
            }
            catch (ApplicationException exception)
            {
                Console.WriteLine(exception.Message);
                throw;
            }
            """;

        var result = new SharpSqlCompiler().Transpile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("BEGIN TRY", result.Sql);
        Assert.Contains("THROW 51012, @_application_exception_message, 1;", result.Sql);
        Assert.Contains("DECLARE @_catch_message NVARCHAR(4000) = ERROR_MESSAGE();", result.Sql);
        Assert.Contains("IF @_catch_number = 51012", result.Sql);
        Assert.Contains("PRINT @_catch_message;", result.Sql);
        Assert.Contains("THROW;", result.Sql);
    }

    [Fact]
    public void MapsUnreservedSqlCatchMetadataToDatabaseException()
    {
        const string source = """
            try
            {
                int zero = 0;
                Console.WriteLine(1 / zero);
            }
            catch (SharpSql.DatabaseException exception)
            {
                Console.WriteLine($"{exception.Number}:{exception.Severity}:{exception.State}:{exception.Procedure}:{exception.LineNumber}:{exception.Message}");
            }
            """;

        var result = new SharpSqlCompiler().Transpile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("IF (@_catch_number < 51000 OR @_catch_number > 51999)", result.Sql);
        Assert.Contains("ERROR_NUMBER()", result.Sql);
        Assert.Contains("ERROR_MESSAGE()", result.Sql);
        Assert.Contains("ERROR_SEVERITY()", result.Sql);
        Assert.Contains("ERROR_STATE()", result.Sql);
        Assert.Contains("ERROR_PROCEDURE()", result.Sql);
        Assert.Contains("ERROR_LINE()", result.Sql);
        Assert.Contains("PRINT CONCAT(@_catch_number", result.Sql);
    }

    [Fact]
    public void PreservesRuntimeExceptionMappingsWhenFilteringCatchClauses()
    {
        const string source = """
            try
            {
                var values = new List<int> { 1 };
                Console.WriteLine(values[2]);
            }
            catch (ArgumentException exception)
            {
                Console.WriteLine(exception.Message);
            }
            """;

        var result = new SharpSqlCompiler().Transpile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("THROW 51002, 'List index was out of range.'", result.Sql);
        Assert.Contains("IF @_catch_number IN (51001, 51002, 51004, 51005, 51006, 51009)", result.Sql);
    }

    [Fact]
    public void LowersOrderedCatchFiltersAndRethrowsUnmatchedErrors()
    {
        const string source = """
            try
            {
                throw new ApplicationException("boom");
            }
            catch (ApplicationException exception) when (exception.Message == "other")
            {
                Console.WriteLine("filtered");
            }
            catch (ApplicationException)
            {
                Console.WriteLine("matched");
            }
            """;

        var result = new SharpSqlCompiler().Transpile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("IF (@_catch_number = 51012) AND (@_catch_message = N'other')", result.Sql);
        Assert.Contains("ELSE IF @_catch_number = 51012", result.Sql);
        Assert.Contains("THROW;", result.Sql);
    }
}
