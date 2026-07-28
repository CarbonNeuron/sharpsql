string? missing = null;
string fallback = missing ?? "fallback";
int? optionalNumber = null;
int resolved = optionalNumber ?? 7;

bool isMissing = missing == null;
bool inRange = resolved >= 5 && resolved < 10;
bool negated = !false;
int selected = inRange ? 11 : 22;

Console.WriteLine($"fallback={fallback}; resolved={resolved}; missing={isMissing}; range={inRange}; negated={negated}; selected={selected}");
