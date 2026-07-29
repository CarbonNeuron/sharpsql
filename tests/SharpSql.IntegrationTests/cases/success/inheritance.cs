var item = new Derived(3);
Base alias = item;
item.Advance(2);
alias.AddBase(1);
Console.WriteLine($"explicit={item.BaseValue}:{item.DerivedValue}:{item.Shared}:{alias.Shared}");

var initialized = new Derived(1) { BaseValue = 30, Shared = 40 };
Base initializedBase = initialized;
Console.WriteLine($"initializer={initialized.BaseValue}:{initialized.Shared}:{initializedBase.Shared}");

var leaf = new Leaf();
Console.WriteLine($"implicit={leaf.Trace}");

class Base
{
    public int BaseValue = 2;
    public int Shared = 10;

    public Base()
    {
        BaseValue++;
    }

    public Base(int value) : this()
    {
        BaseValue += value;
    }

    public void AddBase(int value)
    {
        BaseValue += value;
    }
}

class Derived : Base
{
    public new int Shared = 20;
    public int DerivedValue = 4;

    public Derived(int value) : base(value + 1)
    {
        BaseValue++;
        DerivedValue += BaseValue;
        Shared++;
        base.Shared++;
    }

    public void Advance(int value)
    {
        BaseValue += value;
        DerivedValue += value;
        base.Shared += value;
    }
}

class Root
{
    public int Trace = 1;

    public Root()
    {
        Trace = Trace * 10 + 2;
    }
}

class Middle : Root
{
    public Middle()
    {
        Trace = Trace * 10 + 3;
    }
}

class Leaf : Middle
{
    public Leaf()
    {
        Trace = Trace * 10 + 4;
    }
}
