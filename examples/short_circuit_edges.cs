bool NeverReturns(int value) => NeverReturns(value + 1);

bool IsEven(int value)
{
    if (value == 0) return true;
    return IsOdd(value - 1);
}

bool IsOdd(int value)
{
    if (value == 0) return false;
    return IsEven(value - 1);
}

bool trueWithoutCall = true || NeverReturns(0);
bool falseWithoutCall = false && NeverReturns(0);
bool even = IsEven(12);
bool odd = IsOdd(9);
int selected = even ? 7 : 9;

Console.WriteLine($"short-circuit={trueWithoutCall}:{falseWithoutCall}; parity={even}:{odd}; selected={selected}");
