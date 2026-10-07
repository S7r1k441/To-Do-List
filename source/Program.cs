namespace TaskBoard;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\TaskBoard.SingleInstance.v1", out bool isFirst);
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TaskBoard.Show.v1");
        if (!isFirst)
        {
            // Already running: ask the existing instance to come to the front.
            showSignal.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        bool fromStartup = args.Any(a => a.Equals("--startup", StringComparison.OrdinalIgnoreCase));
        Application.Run(new MainForm(fromStartup, showSignal));
        GC.KeepAlive(mutex);
    }
}
