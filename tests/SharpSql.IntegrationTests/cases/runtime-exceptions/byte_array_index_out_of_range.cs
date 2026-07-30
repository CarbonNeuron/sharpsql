// sharpsql-expect-exception: IndexOutOfRangeException
byte[] values = new byte[] { 4 };
Console.WriteLine("before invalid byte-array index");
Console.WriteLine(values[2]);
