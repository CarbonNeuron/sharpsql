Counter counter = new Counter(2);
Counter alias = counter;
counter.Increment();
alias.Add(3);

string description = counter.Describe();
Console.WriteLine($"{description}; alias={alias.Value}");

class Counter
{
    public Counter(int initial)
    {
        Value = initial;
    }

    public int Value { get; set; }

    public void Increment()
    {
        Value = Value + 1;
    }

    public void Add(int amount)
    {
        Value += amount;
    }

    public string Describe() => "value=" + Value;
}
