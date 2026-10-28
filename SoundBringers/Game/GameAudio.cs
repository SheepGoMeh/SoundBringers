using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SoundBringers.Game;

// Read-only access to the SQEX Sd mixer; signatures verified on game build 2026.10.20
internal static unsafe partial class GameAudio
{
	// hooks
	public const string VoiceSig = "48 8B C4 48 89 58 18 55 56 57 41 54 41 55 41 56 41 57 48 8D A8 D8 F9 FF FF"; // nint (nint voice, nint scratch)
	public const string RenderSig = "40 55 41 54 41 57 48 8D AC 24 A0 F6 FF FF"; // void (nint)

	// endpoint device wrapper +0x28 = inner; WASAPI inner +8 = WAVEFORMATEX*, channels at +2
	private const int DeviceInner = 0x28;
	private const int DeviceFormat = 8;
	private const int FormatChannels = 2;

	public const int SubmixCount = 15;
	public const int MasterCount = 5;

	// Globals resolved through the RIP-relative operand of an instruction inside a function found by signature
	private sealed record Anchor(string Name, string Sig, int Off, int DispOff, int Len, string Prefix);

	private static readonly Anchor[] Anchors =
	[
		new("Rate", "48 89 5C 24 18 56 57 41 57 48 81 EC 10 03 00 00", 207, 4, 8, "66 0F 6E 05"),
		new("Channels", "48 83 EC 28 83 F9 01 75 ?? E8 ?? ?? ?? ??", 24, 2, 6, "8B 05"),
		new("Frames", "8B 05 ?? ?? ?? ?? C3 CC CC CC CC CC CC CC CC CC 48 89 6C 24 18", 0, 2, 6, "8B 05"),
		new("SpatialActive", "40 55 41 54 48 8B EC 48 83 EC 48", 17, 2, 6, "39 05"),
		new("BusSlots", "40 55 41 54 48 8B EC 48 83 EC 48", 79, 3, 7, "4C 8D 3D"),
		new("Devices", "48 83 EC 28 33 D2 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 08", 6, 3, 7, "48 8D 0D"),
		new("Submixes", "48 83 EC 28 48 63 D1 48 8D 0D ?? ?? ?? ??", 7, 3, 7, "48 8D 0D"),
		new("Masters", "48 89 5C 24 18 55 56 57 41 54 41 56 48 83 EC 30 4C 8B E1", 25, 3, 7, "48 8D 0D"),
		new("WasapiVtbl", "40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 48 8D 05 ?? ?? ?? ?? 33 D2 48 89 03 48 8D 4B 28", 14, 3, 7, "48 8D 05"),
		new("MockVtbl", "40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 48 8D 05 ?? ?? ?? ?? 48 89 03 33 C0 48 89 43 08 48 89 43 10 89 43 18", 14, 3, 7, "48 8D 05"),
		new("BusBlock", "3B 0D ?? ?? ?? ?? 75 ?? 48 8B 05 ?? ?? ?? ??", 28, 3, 7, "48 8B 05"),
		new("BusCount", "3B 0D ?? ?? ?? ?? 75 ?? 48 8B 05 ?? ?? ?? ??", 20, 2, 6, "3B 0D"),
	];

	private static readonly Anchor[] OptionalAnchors =
	[
		new("CategoryMode", "8B 05 ?? ?? ?? ?? C3 CC CC CC CC CC CC CC CC CC C2 00 00 CC CC CC CC CC CC CC CC CC CC CC CC CC 40 53", 0, 2, 6, "8B 05"),
		new("CategoryPlayer", "40 53 48 83 EC 50 0F 29 74 24 40 48 8B D9 0F 29 7C 24 30 44 0F 29 44 24 20 F3 44 0F 10 05 ?? ?? ?? ??", 70, 5, 9, "F3 44 0F 10 05"),
		new("CategoryParty", "40 53 48 83 EC 50 0F 29 74 24 40 48 8B D9 0F 29 7C 24 30 44 0F 29 44 24 20 F3 44 0F 10 05 ?? ?? ?? ??", 86, 5, 9, "F3 44 0F 10 05"),
		new("RenderMode", RenderSig, 805, 2, 6, "8B 05"), // dword_142F6A440, read at +0x325 of the render callback: 1 = the game skips its mix pass
		new("CategoryOther", "40 53 48 83 EC 50 0F 29 74 24 40 48 8B D9 0F 29 7C 24 30 44 0F 29 44 24 20 F3 44 0F 10 05 ?? ?? ?? ??", 102, 5, 9, "F3 44 0F 10 05"),
	];

	private static readonly Dictionary<string, nint> Addresses = [];

	// Self/Party/Other globals read by the sound volume (VolumeChain.Scalar); 0 when their anchors did not resolve
	public static nint CategoryMode { get; private set; }

	public static nint CategoryPlayer { get; private set; }

	public static nint CategoryParty { get; private set; }

	public static nint CategoryOther { get; private set; }

	public static nint RenderMode { get; private set; }

	public static bool CategoryGlobals => CategoryMode != 0 && CategoryPlayer != 0 && CategoryParty != 0 && CategoryOther != 0;

	public static bool Ready { get; private set; }

	public static nint VoiceFunction => Addresses["VoiceFn"];

	public static nint RenderFunction => Addresses["RenderFn"];


	[DllImport("kernel32.dll")]
	private static extern bool ReadProcessMemory(nint process, nint address, void* buffer, nuint size, out nuint read);

	public static bool Initialize()
	{
		try
		{
			foreach (Anchor anchor in Anchors)
			{
				Addresses[anchor.Name] = Resolve(anchor);
			}

			// optional: a miss costs the Self/Party/Other balance, not the plugin
			foreach (Anchor anchor in OptionalAnchors)
			{
				try
				{
					Addresses[anchor.Name] = Resolve(anchor);
				}
				catch (Exception e)
				{
					Service.PluginLog.Warning(e, $"Optional anchor {anchor.Name} not resolved");
				}
			}

			CategoryMode = Addresses.GetValueOrDefault("CategoryMode");
			CategoryPlayer = Addresses.GetValueOrDefault("CategoryPlayer");
			CategoryParty = Addresses.GetValueOrDefault("CategoryParty");
			CategoryOther = Addresses.GetValueOrDefault("CategoryOther");
			RenderMode = Addresses.GetValueOrDefault("RenderMode");

			Addresses["VoiceFn"] = Find("VoiceSig", VoiceSig);
			Addresses["RenderFn"] = Find("RenderSig", RenderSig);
			Ready = true;
		}
		catch (Exception e)
		{
			Ready = false;
			Service.PluginLog.Error(e, "Game audio not resolved");
		}

		return Ready;
	}

	// true when the whole span was read
	public static bool TryRead(nint address, Span<byte> destination)
	{
		fixed (byte* p = destination)
		{
			return Read(address, p, destination.Length) == destination.Length;
		}
	}

	// bytes actually read
	public static int Read(nint address, void* buffer, int size)
	{
		ReadProcessMemory(-1, address, buffer, (nuint)size, out nuint read);
		return (int)read;
	}

	public static T Rd<T>(nint address) where T: unmanaged
	{
		T value = default;
		return ReadProcessMemory(-1, address, &value, (nuint)sizeof(T), out nuint read) && read == (nuint)sizeof(T)
			       ? value
			       : default;
	}

	public static nint Submix(int index) =>
		index is >= 0 and < SubmixCount ? Rd<nint>(Addresses["Submixes"] + (8 * index)) : 0;

	public static nint Master(int index) =>
		index is >= 0 and < MasterCount ? Rd<nint>(Addresses["Masters"] + (8 * index)) : 0;

	// every bus pointer (pointers[id - 1]) in one read; the number of buses, 0 when unreadable
	public static int BusPointers(Span<nint> pointers)
	{
		nint block = Rd<nint>(Addresses["BusBlock"]);
		int count = Math.Min(Rd<int>(Addresses["BusCount"]), pointers.Length);
		if (block == 0 || count < 1)
		{
			return 0;
		}

		fixed (nint* p = pointers)
		{
			return Read(block, p, count * sizeof(nint)) == count * sizeof(nint) ? count : 0;
		}
	}

	public static nint SoundManager() => (nint)FFXIVClientStructs.FFXIV.Client.Sound.SoundManager.Instance();

	// the render callback's other branch: no mix pass this period (false when the anchor did not resolve)
	public static bool MixSkipped() => RenderMode != 0 && Rd<int>(RenderMode) == 1;

	public static bool SpatialActive() => Rd<int>(Addresses["SpatialActive"]) != 0;

	public static int MixerChannels() => Rd<int>(Addresses["Channels"]);

	public static int MixerRate() => Rd<int>(Addresses["Rate"]);

	public static int MixerFrames() => Rd<int>(Addresses["Frames"]);

	// 2 when unknown or mock
	public static int EndpointChannels()
	{
		for (int i = 0; i < 2; i++)
		{
			nint wrapper = Rd<nint>(Addresses["Devices"] + (8 * i));
			nint inner = wrapper != 0 ? Rd<nint>(wrapper + DeviceInner) : 0;
			if (inner == 0 || Rd<nint>(inner) != Addresses["WasapiVtbl"])
			{
				continue;
			}

			nint format = Rd<nint>(inner + DeviceFormat);
			int channels = format != 0 ? Rd<ushort>(format + FormatChannels) : 0;
			if (channels > 0)
			{
				return channels;
			}
		}

		return 2;
	}

	private static nint Find(string name, string sig)
	{
		try
		{
			return Service.SigScanner.ScanText(sig);
		}
		catch (KeyNotFoundException)
		{
			throw new InvalidOperationException($"{name}: signature not found");
		}
	}

	private static nint Resolve(Anchor anchor)
	{
		nint at = Find($"anchor {anchor.Name}", anchor.Sig) + anchor.Off;
		byte[] expected = Convert.FromHexString(anchor.Prefix.Replace(" ", string.Empty));
		byte[] actual = new byte[expected.Length];
		Marshal.Copy(at, actual, 0, actual.Length);
		if (!actual.AsSpan().SequenceEqual(expected))
		{
			throw new InvalidOperationException($"anchor {anchor.Name}: instruction differs");
		}

		return at + anchor.Len + Marshal.ReadInt32(at + anchor.DispOff);
	}
}
