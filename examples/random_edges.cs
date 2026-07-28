Random first = new Random(12345);
Random second = new Random(12345);

int firstValue = first.Next();
int matchingValue = second.Next();
int bounded = first.Next(1000);
int ranged = first.Next(-50, 51);
int largeRange = first.Next(-2147483648, 2147483647);
double fraction = first.NextDouble();
int afterFraction = first.Next();

bool seededInstancesMatch = firstValue == matchingValue;
bool fractionInRange = fraction >= 0 && fraction < 1;

int rollTotal = 0;
for (int i = 0; i < 5; i++)
{
    rollTotal += Roll(first);
}

Random unseeded = new Random();
int unseededValue = unseeded.Next(10, 20);
bool unseededInRange = unseededValue >= 10 && unseededValue < 20;

bool repeatedUnseededInRange = true;
for (int i = 0; i < 8; i++)
{
    Random ephemeral = new Random();
    int value = ephemeral.Next(-100, 101);
    repeatedUnseededInRange = repeatedUnseededInRange && value >= -100 && value < 101;
}

Console.WriteLine($"seeded={firstValue}:{bounded}:{ranged}:{largeRange}:{afterFraction}; rolls={rollTotal}; checks={seededInstancesMatch}:{fractionInRange}:{unseededInRange}:{repeatedUnseededInRange}");

int Roll(Random random) => random.Next(1, 7);
