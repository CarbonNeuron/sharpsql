namespace MemoryOptimizedProject;

public static class SqlJob
{
    public static void Main() => Run();

    public static void Run()
    {
        int answer = Add(35, 7);
        Console.WriteLine($"memory-answer={answer}");
    }

    private static int Add(int value, int remaining) =>
        remaining == 0 ? value : Add(value + 1, remaining - 1);
}
