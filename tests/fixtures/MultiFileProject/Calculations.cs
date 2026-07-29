namespace MultiFileProject;

public static class Calculations
{
    public static int Double(int value) => value * 2;

    // An unrelated overload must not make the selected deployment graph ambiguous.
    public static string Double(string value) => value + value;
}
