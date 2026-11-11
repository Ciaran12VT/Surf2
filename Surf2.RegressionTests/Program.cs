namespace Surf2.RegressionTests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--preview" or "--json" or "--document-layout")
            return MasterRegressionSuite.Run(args);

        RelationalRegressionSuite.RunAsync(args).GetAwaiter().GetResult();
        return 0;
    }
}
