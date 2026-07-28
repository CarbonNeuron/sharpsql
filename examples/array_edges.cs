int[] defaults = new int[4];
defaults[0] = 4;
defaults[3] = 8;

int defaultSum = 0;
foreach (int value in defaults)
{
    defaultSum += value;
}

int[] seeded = new int[] { 2, 4, 6 };
seeded[1] = 5;
int seededSum = 0;
foreach (int value in seeded)
{
    seededSum += value;
}

Console.WriteLine($"defaults={defaults.Length}:{defaultSum}; seeded={seeded.Length}:{seeded[1]}:{seededSum}");
