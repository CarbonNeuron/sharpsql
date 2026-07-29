var counter = new Counter(-2) { Steps = 4 };
Console.WriteLine($"counter={counter.Value}:{counter.Steps}:{counter.History.Count}");

var nested = Build(3);
Console.WriteLine($"nested={nested.Value}:{nested.Steps}:{nested.History.Count}");

var numericBox = new Box(4);
var textBox = new Box("hello");
Console.WriteLine($"overloads={numericBox.Value}:{textBox.Value}");

Counter Build(int depth)
{
    if (depth == 0)
        return new Counter(1);
    Counter previous = Build(depth - 1);
    return new Counter(previous.Value);
}

class Counter
{
    public int Value { get; set; } = InitialValue();
    public int Steps { get; set; }
    public List<int> History { get; } = new List<int> { 1 };

    public Counter(int value) : this(value, 2)
    {
        Value++;
    }

    public Counter(int value, int steps)
    {
        if (value < 0)
            value = 0;
        History.Add(value);
        Value += this.Double(value);
        for (int index = 0; index < steps; index++)
            Value += 2;
        Steps = steps;
    }

    private int Double(int value) => value * 2;
    private static int InitialValue() => 5;
}

class Box
{
    public int Value { get; set; }
    public Box(int value) { Value = value; }
    public Box(string value) { Value = value.Length; }
}
