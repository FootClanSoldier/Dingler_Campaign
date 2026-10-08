using System.Collections;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using Dingler.Server.Abstractions;
using Dingler.Server.Pipeline;
using Microsoft.Extensions.Logging;

namespace Dingler.Server;

public sealed class DinglerClient : IDisposable
{
	private readonly TcpClient _client;
	private readonly IncomingPipeline _incomingPipeline;
	private readonly OutgoingPipeline _outgoingPipeline;
	private readonly IStreamHandler _streamHandler;
	private readonly Channel<byte[]> _incomingChannel;
	private readonly Channel<RequestContext> _outgoingChannel;
	private readonly ILogger<DinglerClient>? _logger;
	private readonly TimeSpan _idleTimeout;
	public SessionContext SessionContext { get; }

	
	public DinglerClient(SessionContext sessionContext, TcpClient client,
		IncomingPipeline incomingPipeline, 
		OutgoingPipeline outgoingPipeline,
		IStreamHandler streamHandler,
		TimeSpan idleTimeout,
		ILogger<DinglerClient>? logger = null)
	{
		SessionContext = sessionContext;
		_client = client;
		_incomingPipeline = incomingPipeline;
		_outgoingPipeline = outgoingPipeline;
		_streamHandler = streamHandler;
		_idleTimeout = idleTimeout;
		_logger = logger;
		_incomingChannel = Channel.CreateUnbounded<byte[]>();
		_outgoingChannel = Channel.CreateUnbounded<RequestContext>();
		sessionContext.SendMessageAsync += SendMessageAsync;
		sessionContext.TrySendMessage += TrySendMessage;
	}

	public async Task RunAsync(ICancellationManager cancellationManager)
	{
		using var cts = cancellationManager.CreateLinkedSource();
		var token = cts.Token;
		var stream = _client.GetStream();

		var tasks = new[]
		{
			ReadFromStreamAsync(stream, token),
			WriteToStreamAsync(stream, token),
			HandleRequestsAsync(token)
		};

		try
		{
			await Task.WhenAny(tasks);
		}
		finally
		{
			try
			{
				await cts.CancelAsync();
				await Task.WhenAll(tasks);
			}
			catch (ObjectDisposedException)
			{ }
			catch (OperationCanceledException)
			{ }
		}
	}

	private async Task SendMessageAsync(object message, CancellationToken token)
	{
		var context = new RequestContext(new byte[1], SessionContext)
		{
			ResponseObject = message
		};

		await _outgoingChannel.Writer.WriteAsync(context, token)
			.ConfigureAwait(false);
	}

	private bool TrySendMessage(object message)
	{
		var context = new RequestContext(new byte[1], SessionContext)
		{
			ResponseObject = message
		};
		
		return _outgoingChannel.Writer.TryWrite(context);
	}

	private async Task ReadFromStreamAsync(NetworkStream stream, CancellationToken token)
	{
		try
		{
			while (!token.IsCancellationRequested)
			{
				using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(token);
				idleCts.CancelAfter(_idleTimeout);
				var data = await _streamHandler.ReadAsync(stream, idleCts.Token);
				await _incomingChannel.Writer.WriteAsync(data, token);
			}
		}
		finally
		{
			_incomingChannel.Writer.Complete();
		}
	}

	private async Task WriteToStreamAsync(Stream stream, CancellationToken token)
	{
		try
		{
			await foreach (var message in _outgoingChannel.Reader.ReadAllAsync(token))
			{
				try
				{
					await _outgoingPipeline.Delegate(message, token);
				}
				catch (NotImplementedException e)
				{
					_logger?.LogInformation(
						"Unknown message type received: {exception}",
						e.Message);
					continue;
				}

				if (message.RawResponse is null)
					throw new Exception("Response is null");
				
				await stream.WriteAsync(message.RawResponse, token);
			}
		}
		finally
		{
			_outgoingChannel.Writer.Complete();
		}
	}

	private async Task HandleRequestsAsync(CancellationToken token)
	{
		await foreach (var request in _incomingChannel.Reader.ReadAllAsync(token))
		{
			var requestContext = new RequestContext(request, SessionContext);
			try
			{
				await _incomingPipeline.Delegate(requestContext, token);

				if (requestContext.HasResponse)
				{
					await _outgoingChannel.Writer.WriteAsync(requestContext, token);
				}

				// Some HEX services reply to the triggering request and then emit one or
				// more reqid=0 notifications. Keep that ordering deterministic.
				foreach (var message in SessionContext.DrainMessagesAfterResponse())
				{
					var deferredContext = new RequestContext(new byte[1], SessionContext)
					{
						ResponseObject = message,
					};
					await _outgoingChannel.Writer.WriteAsync(deferredContext, token);
				}
			}
			catch (InvalidOperationException e)
			{
				// Do not let a failed request leak a deferred notify into the next one.
				SessionContext.DrainMessagesAfterResponse();
				LogClusterEnvelopeDiagnostics(requestContext);
				_logger?.LogInformation(
					"Unknown message type received: {exception}",
					e.Message);
			}
		}
	}

	private void LogClusterEnvelopeDiagnostics(RequestContext context)
	{
		var request = context.RequestObject;
		if (request?.GetType().FullName != "Game.Shared.Cluster.ClusterComms+EnvelopeS")
			return;

		try
		{
			context.AdditionalData.TryGetValue("header", out var header);
			context.AdditionalData.TryGetValue("data_type", out var dataType);
			context.AdditionalData.TryGetValue("request_id", out var requestId);

			var builder = new StringBuilder();
			builder.AppendLine("ClusterComms EnvelopeS diagnostic:");
			builder.Append("  RuntimeType: ").AppendLine(request.GetType().AssemblyQualifiedName ?? request.GetType().FullName);
			builder.Append("  DataType: ").AppendLine(dataType?.ToString() ?? "<missing>");
			builder.Append("  RequestId: ").AppendLine(requestId?.ToString() ?? "<missing>");
			builder.Append("  Header: ").AppendLine(header?.ToString() ?? "<missing>");
			builder.AppendLine("  Payload:");

			var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
			AppendObject(builder, request, "    ", 0, visited);

			var clusterType = request.GetType().DeclaringType;
			if (clusterType is not null)
			{
				builder.AppendLine("  ClusterComms contract:");
				AppendTypeContract(builder, clusterType, "    ");
			}

			_logger?.LogInformation("{diagnostic}", builder.ToString().TrimEnd());
		}
		catch (Exception ex)
		{
			// Diagnostics must never alter the existing unknown-request behavior.
			_logger?.LogWarning(ex, "Failed to inspect ClusterComms EnvelopeS");
		}
	}

	private static void AppendTypeContract(StringBuilder builder, Type clusterType, string indent)
	{
		builder.Append(indent).Append("Type: ").AppendLine(clusterType.AssemblyQualifiedName ?? clusterType.FullName ?? clusterType.Name);
		if (clusterType.BaseType is not null)
			builder.Append(indent).Append("BaseType: ").AppendLine(FormatTypeName(clusterType.BaseType));

		builder.Append(indent).AppendLine("DeclaredMembers:");
		AppendDeclaredMembers(builder, clusterType, indent + "  ");

		var allNestedTypes = clusterType
			.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
			.OrderBy(t => t.Name)
			.ToArray();
		var nestedTypes = allNestedTypes.Take(64).ToArray();

		builder.Append(indent).Append("NestedTypes (").Append(allNestedTypes.Length).AppendLine("):");
		foreach (var nestedType in nestedTypes)
			AppendNestedTypeContract(builder, nestedType, indent + "  ", 0);

		if (nestedTypes.Length < allNestedTypes.Length)
			builder.Append(indent).Append("  ... ").Append(allNestedTypes.Length - nestedTypes.Length).AppendLine(" more nested types omitted");
	}

	private static void AppendNestedTypeContract(StringBuilder builder, Type type, string indent, int depth)
	{
		builder.Append(indent).Append(GetTypeVisibility(type)).Append(' ')
			.AppendLine(type.FullName ?? type.Name);
		if (type.BaseType is not null)
			builder.Append(indent).Append("  base ").AppendLine(FormatTypeName(type.BaseType));
		if (type.IsEnum)
		{
			builder.Append(indent).Append("  enum values: ")
				.AppendLine(string.Join(", ", Enum.GetNames(type)));
		}

		AppendDeclaredMembers(builder, type, indent + "  ");

		if (depth >= 2)
			return;

		var allChildren = type
			.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
			.OrderBy(t => t.Name)
			.ToArray();
		if (allChildren.Length == 0)
			return;

		var children = allChildren.Take(32).ToArray();
		builder.Append(indent).Append("  NestedTypes (").Append(allChildren.Length).AppendLine("):");
		foreach (var child in children)
			AppendNestedTypeContract(builder, child, indent + "    ", depth + 1);

		if (children.Length < allChildren.Length)
			builder.Append(indent).Append("    ... ").Append(allChildren.Length - children.Length).AppendLine(" more nested types omitted");
	}

	private static void AppendDeclaredMembers(StringBuilder builder, Type type, string indent)
	{
		var fields = type
			.GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
			.OrderBy(f => f.Name)
			.Take(64)
			.ToArray();
		foreach (var field in fields)
		{
			builder.Append(indent).Append("field ").Append(GetFieldVisibility(field)).Append(' ');
			if (field.IsStatic)
				builder.Append("static ");
			builder.Append(FormatTypeName(field.FieldType)).Append(' ').Append(field.Name);
			if (field.IsStatic && TryFormatStaticFieldValue(field, out var staticValue))
				builder.Append(" = ").Append(staticValue);
			builder.AppendLine();
		}

		var properties = type
			.GetProperties(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
			.OrderBy(p => p.Name)
			.Take(64)
			.ToArray();
		foreach (var property in properties)
		{
			var accessor = property.GetMethod ?? property.SetMethod;
			builder.Append(indent).Append("property ");
			if (accessor is not null)
				builder.Append(GetMethodVisibility(accessor)).Append(' ');
			if (accessor?.IsStatic == true)
				builder.Append("static ");
			builder.Append(FormatTypeName(property.PropertyType)).Append(' ').Append(property.Name);
			if (property.GetIndexParameters().Length > 0)
				builder.Append('[').Append(string.Join(", ", property.GetIndexParameters().Select(FormatParameter))).Append(']');
			builder.AppendLine();
		}

		var constructors = type
			.GetConstructors(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
			.OrderBy(c => c.GetParameters().Length)
			.Take(32)
			.ToArray();
		foreach (var constructor in constructors)
		{
			builder.Append(indent).Append("ctor ").Append(GetMethodVisibility(constructor)).Append(' ');
			if (constructor.IsStatic)
				builder.Append("static ");
			builder.Append(type.Name).Append('(')
				.Append(string.Join(", ", constructor.GetParameters().Select(FormatParameter))).AppendLine(")");
		}

		var methods = type
			.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
			.Where(m => !m.IsSpecialName)
			.OrderBy(m => m.Name)
			.ThenBy(m => m.GetParameters().Length)
			.Take(64)
			.ToArray();
		foreach (var method in methods)
		{
			builder.Append(indent).Append("method ").Append(GetMethodVisibility(method)).Append(' ');
			if (method.IsStatic)
				builder.Append("static ");
			builder.Append(FormatTypeName(method.ReturnType)).Append(' ').Append(method.Name);
			if (method.IsGenericMethodDefinition)
			{
				builder.Append('<')
					.Append(string.Join(", ", method.GetGenericArguments().Select(a => a.Name)))
					.Append('>');
			}
			builder.Append('(').Append(string.Join(", ", method.GetParameters().Select(FormatParameter))).AppendLine(")");
		}
	}

	private static bool TryFormatStaticFieldValue(FieldInfo field, out string value)
	{
		value = string.Empty;
		var type = field.FieldType;
		if (!(type == typeof(string) || type.IsPrimitive || type.IsEnum || type == typeof(Guid) || type == typeof(decimal)))
			return false;

		try
		{
			var raw = field.GetValue(null);
			value = raw switch
			{
				null => "<null>",
				string text => $"\"{text.Replace("\r", "\\r").Replace("\n", "\\n")}\"",
				_ => raw.ToString() ?? "<null>"
			};
			return true;
		}
		catch (Exception ex)
		{
			value = $"<read threw {ex.GetType().Name}>";
			return true;
		}
	}

	private static string FormatParameter(ParameterInfo parameter)
	{
		var prefix = parameter.IsOut ? "out " : parameter.ParameterType.IsByRef ? "ref " : string.Empty;
		var parameterType = parameter.ParameterType.IsByRef
			? parameter.ParameterType.GetElementType() ?? parameter.ParameterType
			: parameter.ParameterType;
		return $"{prefix}{FormatTypeName(parameterType)} {parameter.Name}";
	}

	private static string FormatTypeName(Type type)
	{
		if (type.IsArray)
			return $"{FormatTypeName(type.GetElementType() ?? typeof(object))}[]";
		if (!type.IsGenericType)
			return type.FullName ?? type.Name;

		var name = type.GetGenericTypeDefinition().FullName ?? type.Name;
		var tick = name.IndexOf('`');
		if (tick >= 0)
			name = name[..tick];
		return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FormatTypeName))}>";
	}

	private static string GetTypeVisibility(Type type)
	{
		if (type.IsNestedPublic) return "public";
		if (type.IsNestedPrivate) return "private";
		if (type.IsNestedFamily) return "protected";
		if (type.IsNestedAssembly) return "internal";
		if (type.IsNestedFamORAssem) return "protected internal";
		if (type.IsNestedFamANDAssem) return "private protected";
		return "unknown";
	}

	private static string GetFieldVisibility(FieldInfo field)
	{
		if (field.IsPublic) return "public";
		if (field.IsPrivate) return "private";
		if (field.IsFamily) return "protected";
		if (field.IsAssembly) return "internal";
		if (field.IsFamilyOrAssembly) return "protected internal";
		if (field.IsFamilyAndAssembly) return "private protected";
		return "unknown";
	}

	private static string GetMethodVisibility(MethodBase method)
	{
		if (method.IsPublic) return "public";
		if (method.IsPrivate) return "private";
		if (method.IsFamily) return "protected";
		if (method.IsAssembly) return "internal";
		if (method.IsFamilyOrAssembly) return "protected internal";
		if (method.IsFamilyAndAssembly) return "private protected";
		return "unknown";
	}

	private static void AppendObject(StringBuilder builder, object? value, string indent, int depth, HashSet<object> visited)
	{
		if (value is null)
		{
			builder.Append(indent).AppendLine("<null>");
			return;
		}

		if (depth > 3)
		{
			builder.Append(indent).AppendLine("<max depth>");
			return;
		}

		if (value is byte[] bytes)
		{
			var previewLength = Math.Min(bytes.Length, 128);
			builder.Append(indent)
				.Append("byte[").Append(bytes.Length).Append("] hex=")
				.Append(Convert.ToHexString(bytes.AsSpan(0, previewLength)));
			if (previewLength < bytes.Length)
				builder.Append("...");
			builder.AppendLine();
			return;
		}

		if (value is string text)
		{
			if (text.Length > 512)
				text = text[..512] + "...";
			builder.Append(indent).Append('"').Append(text).AppendLine("\"");
			return;
		}

		var type = value.GetType();
		if (type.IsPrimitive || type.IsEnum || value is decimal || value is Guid || value is DateTime || value is DateTimeOffset)
		{
			builder.Append(indent).Append(value).Append(" (").Append(type.FullName).AppendLine(")");
			return;
		}

		if (!type.IsValueType && !visited.Add(value))
		{
			builder.Append(indent).AppendLine("<cycle>");
			return;
		}

		if (value is IEnumerable enumerable)
		{
			builder.Append(indent).Append(type.FullName).AppendLine(" [");
			var count = 0;
			foreach (var item in enumerable)
			{
				if (count >= 16)
				{
					builder.Append(indent).AppendLine("  ...");
					break;
				}
				builder.Append(indent).Append("  [").Append(count).AppendLine("]");
				AppendObject(builder, item, indent + "    ", depth + 1, visited);
				count++;
			}
			builder.Append(indent).AppendLine("]");
			return;
		}

		builder.Append(indent).Append(type.FullName).AppendLine(" {");

		foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(p => p.Name))
		{
			if (property.GetIndexParameters().Length != 0)
				continue;

			builder.Append(indent).Append("  ").Append(property.Name).Append(" : ").Append(property.PropertyType.FullName).AppendLine();
			try
			{
				AppendObject(builder, property.GetValue(value), indent + "    ", depth + 1, visited);
			}
			catch (Exception ex)
			{
				builder.Append(indent).Append("    <getter threw ").Append(ex.GetType().Name).AppendLine(">");
			}
		}

		foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public).OrderBy(f => f.Name))
		{
			builder.Append(indent).Append("  ").Append(field.Name).Append(" : ").Append(field.FieldType.FullName).AppendLine();
			try
			{
				AppendObject(builder, field.GetValue(value), indent + "    ", depth + 1, visited);
			}
			catch (Exception ex)
			{
				builder.Append(indent).Append("    <field read threw ").Append(ex.GetType().Name).AppendLine(">");
			}
		}

		builder.Append(indent).AppendLine("}");
	}

	public void Dispose()
	{
		_client.Dispose();
		SessionContext.SendMessageAsync -= SendMessageAsync;
		SessionContext.TrySendMessage -= TrySendMessage;
		SessionContext.NotifyDisconnected();
	}
}