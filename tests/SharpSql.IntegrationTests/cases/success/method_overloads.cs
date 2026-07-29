var formatter = new Formatter();
Console.WriteLine(formatter.Format(42));
Console.WriteLine(formatter.Format("forty-two"));

sealed class Formatter
{
    public int Format(int value) => value + 1;
    public int Format(string value) => value.Length;
}
