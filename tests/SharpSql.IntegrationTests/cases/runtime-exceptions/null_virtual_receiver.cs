// sharpsql-expect-exception: NullReferenceException
Base value = null;
Console.WriteLine(value.Read());

class Base
{
    public virtual int Read()
    {
        return 1;
    }
}
