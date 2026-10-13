namespace Dingler.Terminal.Frontend;

public static class Choices
{
	public static class IsRunning
	{
		public const string STOP_SERVER = "Stop Server";
		public const string COLLECT_GARBAGE = "Run garbage collector";
		public const string SEED_DRAFT_BOTS = "Add 7 draft bots (debug)";
	}

	public static class IsNotRunning
	{
		public const string START_SERVER = "Start Server";
	}

	public const string EXIT = "Exit";
}