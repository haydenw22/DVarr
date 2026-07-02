// Quick verification of the bug logic
using System;

class Test
{
    static void Main()
    {
        // Brisbane +10:00
        var off = TimeSpan.FromMinutes(600);
        var utcNow = DateTimeOffset.Parse("2026-01-01T18:00:00Z");
        var localNow = utcNow.ToOffset(off);
        
        Console.WriteLine("UTC: " + utcNow);
        Console.WriteLine("LocalNow: " + localNow);
        Console.WriteLine("LocalNow.Date: " + localNow.Date);
        Console.WriteLine("LocalNow.DateTime: " + localNow.DateTime);
        Console.WriteLine("LocalNow.DateTime.Date: " + localNow.DateTime.Date);
        Console.WriteLine();
        Console.WriteLine("Bug: localNow.Date returns " + localNow.Date + " (UTC midnight)");
        Console.WriteLine("But we want the local calendar date: " + localNow.DateTime.Date);
    }
}
