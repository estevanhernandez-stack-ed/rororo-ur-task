namespace Labs626.UrTask;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Task 1 scaffold checkpoint — proves the build wires up the
        // ROROROblox.PluginContract project reference and Grpc.Net.Client
        // package cleanly. Real entry point (gRPC handshake → event subs
        // → tray + recorder window) lands in tasks 2–10.
        Console.WriteLine("RoRoRo Ur Task v0.1.0 — scaffold build OK.");
        Console.WriteLine("Plugin id: 626labs.ur-task");
        Console.WriteLine("Contract version: 1.0");
        return 0;
    }
}
