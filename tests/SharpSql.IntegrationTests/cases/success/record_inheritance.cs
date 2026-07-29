var student = new Student("Ada", 3);
Person alias = student;
Console.WriteLine($"record={student}:{alias.Name}");

var promoted = student with { Name = "Grace", Grade = 4 };
Person promotedAlias = promoted;
Console.WriteLine($"clone={promoted}:{promotedAlias.Name}");

record Person(string Name);
record Student(string Name, int Grade) : Person(Name);
