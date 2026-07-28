int a = 17;
int b = 5;
int c = 2;

int precedence = a + b * 3;
int grouped = (a + b) * 3;
int nested = a - (b - c);
int quotient = -17 / 5;
int remainder = -17 % 5;

int mutated = 1;
mutated++;
++mutated;
mutated *= 4;
mutated -= 2;

Console.WriteLine($"precedence={precedence}; grouped={grouped}; nested={nested}; division={quotient}; modulo={remainder}; mutated={mutated}");
