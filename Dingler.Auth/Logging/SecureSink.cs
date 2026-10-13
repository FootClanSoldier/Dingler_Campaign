using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace Dingler.Auth.Logging;

public sealed class SecureSink : ILogEventSink, IDisposable
{
	private readonly ILogEventSink _innerSink;

	private static readonly Regex SensitiveData = new Regex(@"(?<=[?&;])(?<key>user|pass|totp)=[^&\s]*",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);
	
	private static readonly Regex LoginQuery = new(
		@"(?<path>/auth/hexlogin)\?\S*",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);
	
	public SecureSink(ILogEventSink innerSink)
	{
		_innerSink = innerSink;
	}
	
	public void Emit(LogEvent logEvent)
	{
		var props = logEvent.Properties.Select(p =>
			new LogEventProperty(p.Key, Scrub(p.Value)));

		var ex = logEvent.Exception is null ? null : ScrubException(logEvent.Exception);

		_innerSink.Emit(new LogEvent(logEvent.Timestamp, logEvent.Level, ex, logEvent.MessageTemplate, props,
			logEvent.TraceId ?? default, logEvent.SpanId ?? default));
	}

	private static LogEventPropertyValue Scrub(LogEventPropertyValue value)
	{
		return value switch
		{
			ScalarValue { Value: string s} => new ScalarValue(ScrubString(s)),
			ScalarValue { Value: Uri u }    => new ScalarValue(ScrubString(u.ToString())),
			StructureValue sv => new StructureValue(
				sv.Properties.Select(p => new LogEventProperty(p.Name, Scrub(p.Value))), sv.TypeTag),
			SequenceValue seq => new SequenceValue(seq.Elements.Select(Scrub)),
			DictionaryValue d => new DictionaryValue(
				d.Elements.Select(kv => KeyValuePair.Create(kv.Key, Scrub(kv.Value)))),
			_ => value
		};
	}

	private static Exception ScrubException(Exception ex)
	{
		var text = ex.ToString();
		var scrubbedText = ScrubString(text);
		return ReferenceEquals(text, scrubbedText) || text == scrubbedText
			? ex
			: new RedactedException(ex.GetType().Name, scrubbedText);
	}

	private static string ScrubString(string s)
	{
		s = LoginQuery.Replace(s, "${path}?[REDACTED]");
		return SensitiveData.Replace(s, "${key}=[REDACTED]");
	}

	public void Dispose() => (_innerSink as IDisposable)?.Dispose();
}