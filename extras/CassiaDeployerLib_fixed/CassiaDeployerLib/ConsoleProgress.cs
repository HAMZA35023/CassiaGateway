namespace CassiaDeployerLib;

public sealed class ConsoleProgress
{
    private readonly object _lock = new();

    public void Info(string message)
    {
        lock (_lock)
            Console.WriteLine(message);
    }

    public void Warn(string message)
    {
        lock (_lock)
        {
            var c = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(message);
            Console.ForegroundColor = c;
        }
    }

    public void Error(string message)
    {
        lock (_lock)
        {
            var c = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(message);
            Console.ForegroundColor = c;
        }
    }
}
