using Microsoft.Data.Sqlite;

var dbPath = args.Length > 0 ? args[0] : "data/taskrunner.db";
using var conn = new SqliteConnection($"Data Source={dbPath}");
conn.Open();

using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
using var reader = cmd.ExecuteReader();
Console.WriteLine("=== Tables ===");
while (reader.Read()) Console.WriteLine(reader.GetString(0));

cmd.CommandText = "SELECT * FROM AuthorizedDevices";
try
{
    using var r2 = cmd.ExecuteReader();
    Console.WriteLine("\n=== AuthorizedDevices ===");
    Console.WriteLine($"Columns: {string.Join(", ", Enumerable.Range(0, r2.FieldCount).Select(i => r2.GetName(i)))}");
    int count = 0;
    while (r2.Read())
    {
        count++;
        for (int i = 0; i < r2.FieldCount; i++)
            Console.WriteLine($"  {r2.GetName(i)}: {r2.GetValue(i)}");
        Console.WriteLine("---");
    }
    Console.WriteLine($"Total: {count}");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}

cmd.CommandText = "SELECT * FROM DeviceSyncLogs LIMIT 5";
try
{
    using var r3 = cmd.ExecuteReader();
    Console.WriteLine("\n=== DeviceSyncLogs (top 5) ===");
    int count = 0;
    while (r3.Read())
    {
        count++;
        for (int i = 0; i < r3.FieldCount; i++)
            Console.WriteLine($"  {r3.GetName(i)}: {r3.GetValue(i)}");
        Console.WriteLine("---");
    }
    Console.WriteLine($"Shown: {count}");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}