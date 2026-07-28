string quote = "O'Brien";
string unicode = "Καλημέρα ☕";
string commentMarkers = "// text, not a comment; /* still text */";
string combined = quote + " | " + unicode;
char finalCharacter = 'Z';

Console.WriteLine($"{combined}; marker={commentMarkers}; char={finalCharacter}");
Console.WriteLine("first line\nsecond line");
