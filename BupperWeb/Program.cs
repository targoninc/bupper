using System.Diagnostics;

namespace BupperWeb;

internal static class Program
{
    private static void Main()
    {
        Directory.SetCurrentDirectory(Path.Combine(Directory.GetCurrentDirectory(), "web"));
        RunCommand("bun", "i");

#if DEBUG
        RunCommand("bun", "run start --watch");
#elif RELEASE
        RunCommand("bun", "run start");
#else
        Console.WriteLine("Running in an unknown or custom configuration mode");
#endif
    }

    private static void RunCommand(string command, string arguments)
    {
        try
        {
            ProcessStartInfo processStartInfo = new()
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using Process process = new();
            process.StartInfo = processStartInfo;
            process.Start();

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            Console.WriteLine(output);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error running command '{command} {arguments}': {ex.Message}");
        }
    }
}