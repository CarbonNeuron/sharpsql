namespace MultiFileProject;

public static class SqlJob
{
    public static void Main() => Run();

    public static void Run()
    {
        int answer = Calculations.Double(21);
        Console.WriteLine($"project={answer}");
    }
}
