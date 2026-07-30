byte[] values = new byte[] { 0, 1, 127, 255 };
values[0] = 42;
values[1] += 2;

byte[] zeroed = new byte[3];
zeroed[2] = 9;

byte[] expected = new byte[] { 42, 3, 127, 255 };
int total = 0;
foreach (byte value in values)
    total += value;
Console.WriteLine($"values={values.Length}:{values[0]}:{values[1]}:{values[3]}:{values.SequenceEqual(expected)}");
Console.WriteLine($"total={total}");
Console.WriteLine($"zeroed={zeroed.Length}:{zeroed[0]}:{zeroed[2]}");

var box = new Blob(new byte[] { 5, 6 });
box.Bytes[1] = 7;
Console.WriteLine($"field={box.Bytes.Length}:{box.Bytes[0]}:{box.Bytes[1]}");

var lookup = new Dictionary<byte[], string>();
lookup.Add(values, "payload");
Console.WriteLine($"dictionary={lookup[values]}");

class Blob
{
    public Blob(byte[] bytes)
    {
        Bytes = bytes;
    }

    public byte[] Bytes { get; set; }
}
