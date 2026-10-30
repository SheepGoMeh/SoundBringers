using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace SoundBringers.Output;

// Under Wine on Linux there is no virtual cable driver: make a PulseAudio/PipeWire null sink instead.
// Its monitor ("Monitor of SoundBringers") is what OBS captures.
//
// The umu container hides pactl, but the Pulse socket is shared with the game (winepulse needs it),
// so this speaks the Pulse native protocol on it directly.
public static class LinuxSink
{
	public const string Name = "SoundBringers";

	private const uint ProtocolVersion = 32;
	private const uint CommandError = 0;
	private const uint CommandReply = 2;
	private const uint CommandAuth = 8;
	private const uint CommandSetClientName = 9;
	private const uint CommandLookupSink = 10;
	private const uint CommandLoadModule = 51;

	// one sink per stream category, in StreamCategory order (BGM .. Performance, then the movie audio that has no slider)
	public static readonly string[] SplitNames =
	[
		"SoundBringers-BGM",
		"SoundBringers-SoundEffects",
		"SoundBringers-Voice",
		"SoundBringers-System",
		"SoundBringers-Ambient",
		"SoundBringers-Performance",
		"SoundBringers-Movie",
	];

	// Wine on a host with a Pulse socket: what the split option needs (Wine on a Mac has neither)
	public static bool Available { get; } = StreamOutput.IsWine && SocketPath() != null;

	// whether a device the output lists is the sink: Wine names the devices "Speakers (<description>)"
	public static bool Matches(string deviceName, string sink) =>
		deviceName.Equals(sink, StringComparison.OrdinalIgnoreCase) || deviceName.EndsWith($"({sink})", StringComparison.OrdinalIgnoreCase);

	public static void Ensure() => Ensure([Name]);

	public static void Ensure(string[] names)
	{
		if (!StreamOutput.IsWine)
			return;

		try
		{
			string? path = SocketPath();
			if (path == null)
			{
				Service.PluginLog.Warning("No PulseAudio socket found, no virtual sink created");
				return;
			}

			using PulseClient client = new(path);
			foreach (string name in names)
			{
				if (client.Exists(name))
					continue;

				client.LoadNullSink(name);
				Service.PluginLog.Information($"Created virtual sink {name}");
			}
		}
		catch (Exception e)
		{
			Service.PluginLog.Error(e, "Creating the virtual sink failed");
		}
	}

	private static string? SocketPath()
	{
		string? server = Environment.GetEnvironmentVariable("PULSE_SERVER");
		if (server != null && server.StartsWith("unix:", StringComparison.Ordinal))
			return ToWinePath(server["unix:".Length..]);

		string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
		if (runtime == null)
			return null;

		return ToWinePath(runtime.TrimEnd('/') + "/pulse/native");
	}

	// the game's environment is the Unix one; Wine sees the Unix root as Z:
	private static string ToWinePath(string unixPath) =>
		unixPath.Length > 1 && unixPath[0] == '/' ? @"Z:" + unixPath.Replace('/', '\\') : unixPath;

	private sealed class PulseClient : IDisposable
	{
		private readonly Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
		private uint tag;

		public PulseClient(string path)
		{
			this.socket.SendTimeout = 3000;
			this.socket.ReceiveTimeout = 3000;
			this.socket.Connect(new UnixDomainSocketEndPoint(path));

			// pipewire-pulse ignores the cookie over a local socket; a zero one satisfies the protocol
			Tags auth = this.Begin(CommandAuth);
			auth.U32(ProtocolVersion);
			auth.Arbitrary(new byte[256]);
			this.Call(auth);

			Tags name = this.Begin(CommandSetClientName);
			name.EmptyProplist();
			this.Call(name);
		}

		public bool Exists(string sink)
		{
			Tags t = this.Begin(CommandLookupSink);
			t.String(sink);
			return this.Call(t, false) != null;
		}

		public void LoadNullSink(string sink)
		{
			Tags t = this.Begin(CommandLoadModule);
			t.String("module-null-sink");
			t.String($"sink_name={sink} sink_properties=device.description={sink} channels=2 rate=48000");
			this.Call(t);
		}

		public void Dispose() => this.socket.Dispose();

		private Tags Begin(uint command)
		{
			Tags t = new();
			t.U32(command);
			t.U32(++this.tag);
			return t;
		}

		// the reply payload, or null on an error reply when throwOnError is false
		private byte[]? Call(Tags request, bool throwOnError = true)
		{
			byte[] body = request.ToArray();
			byte[] frame = new byte[20 + body.Length];
			BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
			BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), 0xFFFFFFFF);
			body.CopyTo(frame, 20);
			this.socket.Send(frame);

			while (true)
			{
				byte[] header = this.ReadExact(20);
				int length = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
				byte[] payload = this.ReadExact(length);
				if (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)) != 0xFFFFFFFF)
					continue; // stream data or notifications are not ours

				// tagstruct: 'L' command, 'L' tag, ...
				if (payload.Length < 10 || payload[0] != (byte)'L')
					throw new IOException("Malformed Pulse reply");

				uint command = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(1));
				uint replyTag = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(6));
				if (replyTag != this.tag)
					continue;

				if (command == CommandReply)
					return payload;

				if (command == CommandError && !throwOnError)
					return null;

				throw new IOException($"Pulse command failed (command {command})");
			}
		}

		private byte[] ReadExact(int count)
		{
			byte[] buffer = new byte[count];
			int read = 0;
			while (read < count)
			{
				int n = this.socket.Receive(buffer, read, count - read, SocketFlags.None);
				if (n == 0)
					throw new IOException("Pulse server closed the connection");

				read += n;
			}

			return buffer;
		}
	}

	private sealed class Tags
	{
		private readonly MemoryStream stream = new();

		public void U32(uint value)
		{
			this.stream.WriteByte((byte)'L');
			Span<byte> b = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(b, value);
			this.stream.Write(b);
		}

		public void String(string value)
		{
			this.stream.WriteByte((byte)'t');
			this.stream.Write(Encoding.UTF8.GetBytes(value));
			this.stream.WriteByte(0);
		}

		public void Arbitrary(byte[] value)
		{
			this.stream.WriteByte((byte)'x');
			Span<byte> b = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(b, (uint)value.Length);
			this.stream.Write(b);
			this.stream.Write(value);
		}

		// 'P' opens a property list, a null string ('N') closes it
		public void EmptyProplist()
		{
			this.stream.WriteByte((byte)'P');
			this.stream.WriteByte((byte)'N');
		}

		public byte[] ToArray() => this.stream.ToArray();
	}
}
