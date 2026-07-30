namespace SdkConsumer;

public static class SqlJob
{
    public static async Task Main()
    {
        var values = new List<int> { 1, 2 };
        var tasks = values.Select(Work).ToList();
        await Task.WhenAll(tasks);
        Console.WriteLine("done");
    }

    private static async Task<int> Work(int value)
    {
        await Task.Delay(value);
        return value + 1;
    }
}
