int Square(int x) { return x * x; }

int Clamp(int val, int lo, int hi)
{
    if (val < lo) return lo;
    if (val > hi) return hi;
    return val;
}

int x = 125;
int squared = Square(5);
int clamped = Clamp(x, 0, 100);
Console.WriteLine($"square={squared}, clamp={clamped}");
