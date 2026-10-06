#if RELATIONAL_COMPARISON_PROTOTYPE
global using System;
global using System.Collections.Generic;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;

internal static class ComparisonPrototypes
{
    private static async Task Main(string[] args)
    {
        var checks = await Surf2.Services.RelationalComparison.ComparisonContractChecks.RunAsync();
        foreach (string check in checks) System.Console.WriteLine(check);
        System.Console.WriteLine($"Passed {checks.Count} bounded comparison checks.");
        var scratchChecks = await Surf2.Services.RelationalComparison.ComparisonScratchContractChecks.RunAsync();
        foreach (string check in scratchChecks) System.Console.WriteLine(check);
        System.Console.WriteLine($"Passed {scratchChecks.Count} comparison scratch filesystem checks.");
        var windowChecks = await Surf2.Services.RelationalComparison.ComparisonWindowContractChecks.RunAsync(args.SingleOrDefault());
        foreach (string check in windowChecks) System.Console.WriteLine(check);
        System.Console.WriteLine($"Passed {windowChecks.Count} isolated WPF comparison lifecycle checks.");
    }
}
#endif
