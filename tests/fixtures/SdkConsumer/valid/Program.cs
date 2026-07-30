namespace SdkConsumer;

public static class SqlJob
{
    public static void Main() => Run();

    public static void Run()
    {
        try
        {
            int answer = 6 * 7;
            Console.WriteLine($"answer={answer}");
        }
        catch (SharpSql.DatabaseException exception)
        {
            Console.WriteLine(
                $"database-error={exception.Number}:{exception.Severity}:{exception.State}:" +
                $"{exception.Procedure}:{exception.LineNumber}:{exception.Message}");
        }
    }
}
