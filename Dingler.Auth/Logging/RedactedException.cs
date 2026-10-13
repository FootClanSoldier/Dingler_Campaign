namespace Dingler.Auth.Logging;

public sealed class RedactedException : Exception
{
	private readonly string _full;

	public RedactedException(string type, string full) : base($"[{type}] (redacted)")
	{
		_full = full;
	}

	public override string ToString() => _full;
	public override string? StackTrace => null;
}