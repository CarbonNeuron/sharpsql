int GreatestCommonDivisor(int left, int right)
{
    while (right != 0)
    {
        int remainder = left % right;
        left = right;
        right = remainder;
    }
    return left;
}

int Clamp(int value, int minimum, int maximum)
{
    if (value < minimum) return minimum;
    if (value > maximum) return maximum;
    return value;
}

int gcd = GreatestCommonDivisor(54, 24);
int low = Clamp(-4, 0, 10);
int middle = Clamp(7, 0, 10);
int high = Clamp(40, 0, 10);
Console.WriteLine($"gcd={gcd}; clamp={low},{middle},{high}");
