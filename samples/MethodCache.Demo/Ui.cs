namespace MethodCache.Demo;

/// <summary>Console output for the tour. Colors are skipped when output is redirected or NO_COLOR is set.</summary>
internal static class Ui
{
    private static readonly bool UseColor =
        !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;

    public static int Failures { get; private set; }

    public static void Banner()
    {
        WriteLine();
        WriteLine("  MethodCache, a guided tour", ConsoleColor.Cyan);
        WriteLine("  Add caching to any .NET method with one attribute. Every number below is measured live.", ConsoleColor.DarkGray);
    }

    public static void Chapter(int number, string title, string lead)
    {
        WriteLine();
        WriteLine($"  {number}. {title}", ConsoleColor.Yellow);
        WriteLine($"  {new string('─', title.Length + 3)}", ConsoleColor.DarkYellow);
        WriteLine($"  {lead}", ConsoleColor.Gray);
        WriteLine();
    }

    public static void Code(string code)
    {
        foreach (var line in code.Trim('\n').Split('\n'))
        {
            WriteLine($"    {line.TrimEnd()}", ConsoleColor.DarkCyan);
        }

        WriteLine();
    }

    public static void Step(string label, string value)
    {
        Write($"    {label,-34}", ConsoleColor.Gray);
        WriteLine(value, ConsoleColor.White);
    }

    public static void Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var all = rows.ToList();
        var widths = headers.Select((h, i) => Math.Max(h.Length, all.Count == 0 ? 0 : all.Max(r => r[i].Length))).ToArray();

        WriteLine("    " + string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))), ConsoleColor.DarkGray);
        foreach (var row in all)
        {
            WriteLine("    " + string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))), ConsoleColor.White);
        }

        WriteLine();
    }

    /// <summary>Prints a checked claim. A false claim fails the run, so CI catches a demo that no longer tells the truth.</summary>
    public static void Check(bool condition, string claim)
    {
        if (condition)
        {
            Write("    ✓ ", ConsoleColor.Green);
            WriteLine(claim, ConsoleColor.Green);
        }
        else
        {
            Failures++;
            Write("    ✗ ", ConsoleColor.Red);
            WriteLine($"{claim} (did not hold)", ConsoleColor.Red);
        }
    }

    public static void Summary(int chapters)
    {
        WriteLine();
        if (Failures == 0)
        {
            WriteLine($"  All {chapters} chapters behaved as described.", ConsoleColor.Green);
        }
        else
        {
            WriteLine($"  {Failures} check(s) failed.", ConsoleColor.Red);
        }

        WriteLine();
        WriteLine("  Next: the README has the full feature list, and samples/MethodCache.WebDemo shows the same", ConsoleColor.Gray);
        WriteLine("  services in ASP.NET Core with admin endpoints, OpenTelemetry and Redis.", ConsoleColor.Gray);
        WriteLine();
    }

    public static void WriteEntries(IReadOnlyList<MethodCache.CacheEntryInfo> entries) =>
        Table(
            ["Key", "Expires in", "Tags"],
            entries.Select(e => (IReadOnlyList<string>)[e.StoredKey, Duration(e.TimeToLive), string.Join(", ", e.Tags)]));

    public static string Times(int count) => count == 1 ? "once" : $"{count} times";

    public static string Ms(TimeSpan elapsed) => $"{elapsed.TotalMilliseconds,7:0.0} ms";

    public static string Duration(TimeSpan? duration) => duration switch
    {
        null => "-",
        { TotalHours: >= 1 } d => $"{d.TotalHours:0.#} h",
        { TotalMinutes: >= 1 } d => $"{d.TotalMinutes:0.#} min",
        { } d => $"{d.TotalSeconds:0.#} s",
    };

    private static void WriteLine(string text = "", ConsoleColor color = ConsoleColor.Gray)
    {
        Write(text, color);
        Console.WriteLine();
    }

    private static void Write(string text, ConsoleColor color)
    {
        if (UseColor)
        {
            Console.ForegroundColor = color;
            Console.Write(text);
            Console.ResetColor();
        }
        else
        {
            Console.Write(text);
        }
    }
}
