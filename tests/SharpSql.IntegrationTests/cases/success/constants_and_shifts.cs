int zero = default;
bool flag = default(bool);
string? text = default;
string member = nameof(zero);
int source = 3;
int negative = -8;
int left = source << 2;
int right = negative >> 1;
int masked = 1 << 33;
int sign = 1 << 31;
long longNegative = -9223372036854775807L - 1;
long longRight = longNegative >> 63;
string category = source switch
{
    < 0 => "negative",
    0 => "zero",
    > 0 and < 10 => "small",
    _ => "large"
};

Console.WriteLine($"{zero}:{flag}:{text}:{member}:{left}:{right}:{masked}:{sign}:{longRight}:{category}");
