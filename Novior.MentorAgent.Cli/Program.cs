using Microsoft.Extensions.Logging;
using Novior.MentorAgent.Scanner;

// Story 1637 — novior-mentor: the command line of Novior.MentorAgent. The work is in the scanner.
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};
using var loggers = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
}));
return await MentorUiScanCommand.RunAsync(args, loggers, Console.Error, cancel.Token);
