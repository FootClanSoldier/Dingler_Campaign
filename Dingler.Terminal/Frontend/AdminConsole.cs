using Dingler.Game.Tournaments;
using Dingler.Server;
using Spectre.Console;

namespace Dingler.Terminal.Frontend
{
    // This is largely useless lol
    // I watched a demo on Spectre and went "Wow so neat! I love this! I can make an admin control console!"
    // This should 10000% be an api with a separate frontend so I can access it from a remote pc. I'm going to be
    // deploying to actual big boy servers and do I really want to log in every time there's an issue?
    // This is bush league, past me. Do better
    public sealed class AdminConsole
    {
        private readonly ServerLifetimeManager _manager;

        public AdminConsole(ServerLifetimeManager manager)
        {
            _manager = manager;
        }

        public async Task RunAsync()
        {
            while (true)
            {
                DisplayHeader();

                var status = _manager.IsServerRunning ? "[green]Running[/]" : "[red]Stopped[/]";
                AnsiConsole.MarkupLine($"Server status: {status}");

                var choices = new List<string>();

                choices.AddRange(_manager.IsServerRunning
                    ? new string[] { Choices.IsRunning.STOP_SERVER, Choices.IsRunning.COLLECT_GARBAGE, Choices.IsRunning.SEED_DRAFT_BOTS }
                    : new string[] { Choices.IsNotRunning.START_SERVER });

                choices.Add(Choices.EXIT);

                var choice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                    .Title("Select an action:")
                    .AddChoices(choices));

                switch (choice)
                {
                    case Choices.IsNotRunning.START_SERVER:
                        Console.WriteLine();
                        
                        await AnsiConsole.Status().StartAsync("Starting server. Please wait", context => _manager.StartServerAsync());
                        break;
                    case Choices.IsRunning.STOP_SERVER:
                        await _manager.StopServerAsync().ConfigureAwait(false);
                        break;
                    case Choices.IsRunning.COLLECT_GARBAGE:
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                        GC.Collect(2, GCCollectionMode.Forced, true);
                        break;
                    case Choices.IsRunning.SEED_DRAFT_BOTS:
                        await SeedDraftBotsAsync();
                        break;
                    case Choices.EXIT:
                        await _manager.StopServerAsync().ConfigureAwait(false);
                        return;
                }
            }
        }

        private async Task SeedDraftBotsAsync()
        {
            var tournamentManager = _manager.GetServerService<TournamentManager>();
            if (tournamentManager is null)
            {
                AnsiConsole.MarkupLine("[red]Tournament manager is not available (server stopped?).[/]");
            }
            else
            {
                await tournamentManager.SeedDraftBotsAsync(7);
                AnsiConsole.MarkupLine("[green]Registered 7 draft bots.[/]");
            }

            await Task.Delay(1000);
        }

        private void DisplayHeader()
        {
            Console.Clear();

            AnsiConsole.Write(new FigletText("DINGLER").Centered().Color(Color.Yellow));

            var statusColor = _manager.IsServerRunning ? Color.Green : Color.Red;
            var statusText = _manager.IsServerRunning ? "RUNNING" : "STOPPED";

            AnsiConsole.Write(new Rule($"[{statusColor}]{statusText}[/]").RuleStyle(Style.Parse($"{statusColor}")));
            AnsiConsole.WriteLine();
        }
    }
}
