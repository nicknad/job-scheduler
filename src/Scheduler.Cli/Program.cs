using Scheduler.Cli;

string[] commandArgs = StripApiArgument(args, out string baseUrl);
using HttpClient http = new()
{
    BaseAddress = new Uri(baseUrl, UriKind.Absolute),
    Timeout = TimeSpan.FromSeconds(30),
};

return await SchedulerCli.RunAsync(
    commandArgs,
    new HttpSchedulerApiClient(http),
    Console.Out,
    Console.Error,
    CancellationToken.None);

static string[] StripApiArgument(string[] args, out string baseUrl)
{
    baseUrl = Environment.GetEnvironmentVariable("SCHEDULER_API_URL") ?? "http://localhost:5000";
    List<string> remaining = [];
    for (int index = 0; index < args.Length; index++)
    {
        if (args[index] == "--api" && index + 1 < args.Length)
        {
            baseUrl = args[++index];
            continue;
        }

        remaining.Add(args[index]);
    }

    return [.. remaining];
}
