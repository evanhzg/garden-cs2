using System; using CounterStrikeSharp.API.Modules.Utils; class Program { static void Main() { foreach(var name in Enum.GetNames(typeof(PlayerButtons))) Console.WriteLine(name); } }
