int total = 0;
for (int i = 0; i < 4; i++)
{
    if (i == 1) continue;

    int j = 0;
    while (j < 4)
    {
        j++;
        if (j == 3) break;
        total += i * 10 + j;
    }
}

int step = 0;
do
{
    step++;
    if (step == 2) continue;
    total += step;
} while (step < 3);

Console.WriteLine($"nested-total={total}; final-step={step}");
