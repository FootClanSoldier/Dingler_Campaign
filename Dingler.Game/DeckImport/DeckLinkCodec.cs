using System.Text;

namespace Dingler.Game.DeckImport;

/// <summary>
/// Reads a Hex Codex deck link code ("v1" + base64url(body + CRC-32)).
/// Only structure is validated; card IDs are resolved through <see cref="SiteIds"/>.
///   body  = uv(format) uv(champion) list(main) list(reserves) section*
///   list  = uv(n) entry*n     entry = uv(idGap) uv(copies*2 + hasGems) [ uv(k) uv(gemId)*k ]
///   section = uv(type) uv(length) byte*length   (type 1 = the deck's name; odd types may be skipped)
/// </summary>
public static class DeckLinkCodec
{
	public sealed record Entry(int Id, int Copies, IReadOnlyList<int> Gems);

	public sealed record Deck(int Format, int Champion, IReadOnlyList<Entry> Main, IReadOnlyList<Entry> Reserves, string? Name);

	public sealed class DeckLinkException(string message) : Exception(message);

	private const int UvMax = (1 << 28) - 1;
	private const string B64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

	/// <summary>The code inside a pasted text: a whole link (…/deck/?d=…, #d=…) or a bare code; spaces removed.</summary>
	public static string? FindCode(string input)
	{
		var s = new string(input.Where(c => !char.IsWhiteSpace(c)).ToArray());
		var d = s.IndexOf("d=", StringComparison.Ordinal);
		if (s.Contains("/deck", StringComparison.Ordinal) && d >= 0)
			s = s[(d + 2)..];
		var amp = s.IndexOfAny(new[] { '&', '#' });
		if (amp >= 0) s = s[..amp];
		return s.StartsWith('v') ? s : null;
	}

	public static Deck Decode(string code)
	{
		if (code.Length < 2 || code[0] != 'v' || !char.IsDigit(code[1]))
			throw new DeckLinkException("not a deck link code");
		if (code[1] != '1')
			throw new DeckLinkException($"made by a newer version of Hex Codex (version {code[1]})");
		var bytes = B64Decode(code[2..]);
		if (bytes.Length < 8) throw new DeckLinkException("the code is too short");
		var end = bytes.Length - 4;
		var want = (uint)(bytes[end] << 24 | bytes[end + 1] << 16 | bytes[end + 2] << 8 | bytes[end + 3]);
		if (Crc32(bytes, end) != want) throw new DeckLinkException("the code is damaged (checksum)");

		var pos = 0;
		int Uv()
		{
			int value = 0, mul = 1;
			for (var i = 0; i < 4; i++)
			{
				if (pos >= end) throw new DeckLinkException("the code is damaged (cut off)");
				var b = bytes[pos++];
				value += (b & 0x7f) * mul;
				if ((b & 0x80) == 0) return value;
				mul *= 128;
			}
			throw new DeckLinkException("the code is damaged (number too long)");
		}

		var format = Uv();
		var champion = Uv();
		var lists = new List<Entry>[2] { new(), new() };
		foreach (var list in lists)
		{
			var n = Uv();
			var prev = 0;
			for (var i = 0; i < n; i++)
			{
				var id = prev + Uv();
				if (id <= 0 || id > UvMax) throw new DeckLinkException("the code is damaged (card id)");
				var head = Uv();
				var copies = head / 2;
				if (copies < 1) throw new DeckLinkException("the code is damaged (0 copies)");
				var gems = new List<int>();
				if (head % 2 == 1)
				{
					var k = Uv();
					for (var j = 0; j < k; j++) gems.Add(Uv());
				}
				list.Add(new Entry(id, copies, gems));
				prev = id;
			}
		}

		string? name = null;
		while (pos < end)
		{
			var type = Uv();
			var len = Uv();
			if (pos + len > end) throw new DeckLinkException("the code is damaged (section)");
			if (type == 1)
				name = Encoding.UTF8.GetString(bytes, pos, len);
			else if (type % 2 == 0)
				throw new DeckLinkException($"needs a newer server (section {type})");
			pos += len;
		}
		return new Deck(format, champion, lists[0], lists[1], name);
	}

	private static byte[] B64Decode(string s)
	{
		if (s.Length % 4 == 1) throw new DeckLinkException("the code is damaged (length)");
		var output = new List<byte>();
		for (var i = 0; i < s.Length; i += 4)
		{
			var chunk = s.Substring(i, Math.Min(4, s.Length - i));
			var n = 0;
			for (var j = 0; j < 4; j++)
			{
				var v = j < chunk.Length ? B64.IndexOf(chunk[j]) : 0;
				if (v < 0) throw new DeckLinkException($"the code has a character that isn't allowed ('{chunk[j]}')");
				n = n << 6 | v;
			}
			for (var j = 0; j < chunk.Length - 1; j++) output.Add((byte)(n >> 16 - 8 * j & 0xff));
		}
		return output.ToArray();
	}

	private static uint Crc32(byte[] bytes, int length)
	{
		var c = 0xffffffffu;
		for (var i = 0; i < length; i++)
		{
			c ^= bytes[i];
			for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ c >> 1 : c >> 1;
		}
		return c ^ 0xffffffffu;
	}
}
