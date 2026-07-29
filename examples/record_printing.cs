using System.Linq;

var inventory = new List<InvItem>
{
    new InvItem("Wood", 64),
    new InvItem("Iron", 64),
    new InvItem("Gold", 256),
    new InvItem("Gold", 254)
};

foreach (var item in inventory
             .Where(item => item.Quantity > 12)
             .OrderByDescending(item => item.Quantity)
             .Take((int)(inventory.Count * 0.5)))
{
    Console.WriteLine(item);
}

record InvItem(string Name, int Quantity);
