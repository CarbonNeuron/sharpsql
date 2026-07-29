// sharpsql-expect-diagnostics: SS1001
Console.WriteLine("valid C# overloads");

sealed class Formatter
{
    public string Format(int value) => value.ToString();
    public string Format(string value) => value;
}
