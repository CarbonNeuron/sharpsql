using System.Linq;

var inventory = new List<InvItem>
{
    new("Wood", 64),
    new("Iron", 64),
    new("Gold", 256)
};

var random = new Random(4);
for (int i = 0; i < 10; i++)
{
    var item = inventory[random.Next(0, inventory.Count - i)];
    inventory.Add(item with
    {
        Quantity = random.Next(
            inventory.Min(candidate => candidate.Quantity),
            inventory.Max(candidate => candidate.Quantity))
    });
}

foreach (var item in inventory
             .Where(candidate => candidate.Quantity > 12)
             .OrderByDescending(candidate => candidate.Quantity)
             .Take((int)(inventory.Count * 0.2)))
{
    Console.WriteLine(item);
}

record InvItem(string Name, int Quantity);
