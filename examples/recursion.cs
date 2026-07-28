int Fibonacci(int n)
{
    if (n < 2) return n;
    return Fibonacci(n - 1) + Fibonacci(n - 2);
}

int Factorial(int n) => n <= 1 ? 1 : n * Factorial(n - 1);

int SumDown(int n)
{
    if (n < 0) return SumDown(-n);
    int total = 0;
    while (n > 0)
    {
        total += n;
        n--;
    }
    return total;
}

int fibonacci = Fibonacci(10);
int factorial = Factorial(6);
int sum = SumDown(5);
Console.WriteLine($"fib(10)={fibonacci}, factorial(6)={factorial}, sum(5)={sum}");
if (Fibonacci(5) == 5) Console.WriteLine("top-level continuation works");
