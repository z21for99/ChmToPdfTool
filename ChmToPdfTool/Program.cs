namespace ChmToPdfTool;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && string.Equals(args[0], "--apartment-state", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(Thread.CurrentThread.GetApartmentState());
            return 0;
        }

        if (args.Length == 1 && string.Equals(args[0], "--smoke-ui", StringComparison.OrdinalIgnoreCase))
        {
            ApplicationConfiguration.Initialize();
            using var form = new MainForm();
            form.CreateControl();
            Console.WriteLine("UI smoke test passed");
            return 0;
        }

        if (args.Length == 3 && string.Equals(args[0], "--convert", StringComparison.OrdinalIgnoreCase))
        {
            return ConvertFromCommandLineAsync(args[1], args[2]).GetAwaiter().GetResult();
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    private static async Task<int> ConvertFromCommandLineAsync(string input, string output)
    {
        try
        {
            var progress = new Progress<ConversionProgress>(update =>
                Console.WriteLine($"[{update.Completed}/{update.Total}] {update.Message}"));
            await new ChmConverter().ConvertAsync(input, output, progress);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
