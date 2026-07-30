using SharpSql.SqlServer;
using Xunit;

namespace SharpSql.Tests;

public sealed class SqlBatchOutputTests
{
    [Fact]
    public void NormalizesCapturedOutputAndSqlMessagesIdentically()
    {
        Assert.Equal("first\nsecond", SqlBatchOutput.Normalize("first\r\nsecond\r\n"));
        Assert.Equal("first\nsecond", SqlBatchOutput.FromMessages(["first", "second"]));
    }

    [Fact]
    public void ExecutionResultExposesNormalizedStandardOutput()
    {
        var result = new SqlBatchExecutionResult(true, ["first\r\ncontinued", "second"]);

        Assert.Equal("first\ncontinued\nsecond", result.StandardOutput);
    }
}
