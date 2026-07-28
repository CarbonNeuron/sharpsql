decimal price = 12.5m;
decimal doubledPrice = price * 2m;
double ratio = 5.0 / 2.0;
float single = 1.25f * 2f;
long large = 3000000000L + 2L;
uint unsigned = 4000000000U;

bool decimalMatches = doubledPrice == 25m;
bool doubleMatches = ratio > 2.49 && ratio < 2.51;
bool floatMatches = single == 2.5f;

Console.WriteLine($"numeric={decimalMatches}:{doubleMatches}:{floatMatches}; large={large}; unsigned={unsigned}");
