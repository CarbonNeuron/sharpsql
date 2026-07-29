using System.Linq;

Base left = new Derived(3);
Base right = new Alternate(4);
IValue leftInterface = left;
IValue rightInterface = right;
IValue explicitInterface = new ExplicitValue(7);
Shape shape = new Square(5);

left.Apply(2);
right.Apply(2);

Console.WriteLine($"virtual={left.Read()}:{right.Read()}:{left.ReadThroughBase()}:{right.ReadThroughBase()}");
Console.WriteLine($"interface={leftInterface.Read()}:{rightInterface.Read()}");
Console.WriteLine($"abstract={shape.Measure()}:{explicitInterface.Read()}");
Console.WriteLine($"state={left.Value}:{right.Value}");
var virtualValues = new List<Base> { left, right };
var interfaceValues = new List<IValue> { leftInterface, rightInterface, explicitInterface };
Console.WriteLine($"linq={virtualValues.Select(value => value.Read()).Sum()}:{interfaceValues.Select(value => value.Read()).Sum()}");

interface IValue
{
    int Read();
}

class Base : IValue
{
    public int Value;

    public Base(int value)
    {
        Value = value;
    }

    public virtual int Read()
    {
        return Value + 1;
    }

    public int ReadThroughBase()
    {
        return Read();
    }

    public virtual void Apply(int value)
    {
        Value += value;
    }
}

class Derived : Base
{
    public Derived(int value) : base(value) { }

    public override int Read()
    {
        return base.Read() + 10;
    }

    public override void Apply(int value)
    {
        base.Apply(value);
        Value *= 2;
    }
}

class Alternate : Base
{
    public Alternate(int value) : base(value) { }

    public override int Read()
    {
        return Value + 100;
    }
}

class ExplicitValue : IValue
{
    private int Value;

    public ExplicitValue(int value)
    {
        Value = value;
    }

    int IValue.Read()
    {
        return Value + 1000;
    }
}

abstract class Shape
{
    public abstract int Measure();
}

sealed class Square : Shape
{
    private int Side;

    public Square(int side)
    {
        Side = side;
    }

    public override int Measure()
    {
        return Side * Side;
    }
}
