using Xunit;

namespace SharpSql.Tests;

public sealed class DatabaseExceptionTests
{
    [Fact]
    public void ExposesSqlCatchMetadataAsAnOrdinaryException()
    {
        var inner = new InvalidOperationException("inner");

        var exception = new DatabaseException(
            2627,
            "duplicate key",
            severity: 14,
            state: 1,
            procedure: "InsertPerson",
            lineNumber: 23,
            innerException: inner);

        Assert.IsAssignableFrom<Exception>(exception);
        Assert.Equal(2627, exception.Number);
        Assert.Equal("duplicate key", exception.Message);
        Assert.Equal(14, exception.Severity);
        Assert.Equal(1, exception.State);
        Assert.Equal("InsertPerson", exception.Procedure);
        Assert.Equal(23, exception.LineNumber);
        Assert.Same(inner, exception.InnerException);
    }
}
