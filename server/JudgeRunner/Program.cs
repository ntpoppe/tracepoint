static class Program
{
    public static int Main(string[] args)
    {
        // Toggle cleanup: pass "--keep" to inspect the workspace afterwards.
        bool keep = args.Any(a => a.Equals("--keep", StringComparison.OrdinalIgnoreCase));

        var runner = new JudgeRunner();
        var result = runner.Run(keep);

        Console.WriteLine(result.Json);
        return result.ExitCode;
    }
}

 
