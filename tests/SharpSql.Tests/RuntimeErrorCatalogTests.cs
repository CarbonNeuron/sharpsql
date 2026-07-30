using Xunit;

namespace SharpSql.Tests;

public sealed class RuntimeErrorCatalogTests
{
    [Theory]
    [InlineData(51003, "IndexOutOfRangeException")]
    [InlineData(50000, "SqlException")]
    public void NormalizesSqlFailures(int number, string expectedType)
    {
        var failure = RuntimeErrorCatalog.NormalizeSqlFailure(number, "failure");

        Assert.Equal(expectedType, failure.Type);
        Assert.Equal("failure", failure.Message);
        Assert.Equal(number, failure.Code);
    }
}
