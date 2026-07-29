namespace ServiceBrokerProject;

public static class SqlJob
{
    public static async Task Main()
    {
        var values = new List<int> { 1, 2 };
        var tasks = values.Select(Work).ToList();
        await Task.WhenAll(tasks);
        Console.WriteLine("done");
    }

    public static async Task Alternate()
    {
        var values = new List<int> { 3, 4 };
        var tasks = values.Select(Work).ToList();
        await Task.WhenAll(tasks);
        Console.WriteLine("alternate");
    }

    private static async Task<int> Work(int value)
    {
        await Task.Delay(value);
        return value + 1;
    }
}
