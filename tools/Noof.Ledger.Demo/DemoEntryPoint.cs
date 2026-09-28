namespace Noof.Ledger.Demo;

internal static class DemoEntryPoint
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["start"] => await DemoCommands.StartAsync(),
                ["refresh"] => await DemoCommands.RefreshAsync(),
                ["shots"] => await Shots.ShotsCommand.RunAsync(),
                _ => Usage(),
            };
        }
        catch (InvalidOperationException refused)
        {
            Console.Error.WriteLine(refused.Message);
            return 1;
        }
    }

    static int Usage()
    {
        Console.Error.WriteLine("Usage: Noof.Ledger.Demo start | refresh | shots");
        return 2;
    }
}
