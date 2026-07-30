// sharpsql-expect-exception: OverflowException
int length = -1;
byte[] values = new byte[length];
Console.WriteLine(values.Length);
