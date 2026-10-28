using System;
using System.Buffers.Binary;
using SoundBringers.Dsp;
using SoundBringers.Mix;

namespace SoundBringers.Game;

// What the volume chain reads outside the voice (bus blocks, SoundManager factors, Self/Party/Other globals), from the period's slot.
// Worker only.
internal sealed class VolumeCache
{
	public const int BusBytes = 0xB8;
	private const int Buses = PeriodSlot.Buses;

	private readonly long[] stamps = new long[Buses];
	private readonly FaderTracker faders = new();
	private PeriodSlot? slot;
	private long period;

	public bool HasManager => this.slot!.HasManager;

	public bool HasGlobals => this.slot!.HasGlobals;

	public int Mode => this.slot!.Mode;

	public float Player => this.slot!.Player;

	public float Party => this.slot!.Party;

	public float Other => this.slot!.Other;

	// SoundManager.ActiveVolume, 0 when the manager could not be read
	public float ActiveVolume => this.HasManager ? BinaryPrimitives.ReadSingleLittleEndian(this.Manager[0x38..]) : 0f;

	public ReadOnlySpan<byte> Manager => this.slot!.HasManager ? this.slot.At(this.slot.ManagerOffset, PeriodSlot.ManagerBytes) : default;

	public void Begin(PeriodSlot period)
	{
		this.period++;
		this.slot = period;
	}

	// periods were dropped: the faders have to settle again
	public void Gap()
	{
		this.period++;
		this.faders.Reset();
	}

	// the bus's game fader 1 has not moved for a while (see VolumeFollow); read the bus first this period
	public bool Settled(int id) => this.faders.Settled(id);

	// the bus's block as of this period, empty when there is none
	public ReadOnlySpan<byte> Bus(int id)
	{
		if (id < 1 || id > Buses || this.slot!.BusOffsets[id - 1] < 0)
		{
			return default;
		}

		ReadOnlySpan<byte> block = this.slot.At(this.slot.BusOffsets[id - 1], BusBytes);
		if (this.stamps[id - 1] != this.period)
		{
			this.stamps[id - 1] = this.period;

			// once per period: has the game's first fader (slider, mutes, background mute) been steady? (Count at +0x14 of its dynamic value)
			this.faders.Observe(
				id,
				VolumeChain.Fader(block, GameAudio.BusVolume),
				BinaryPrimitives.ReadInt32LittleEndian(block[(GameAudio.BusVolume + 0x14)..]));
		}

		return block;
	}
}

// A voice's volume recomputed from its sound's terms, with the bus's first fader swapped for the stream's category level
internal static class VoiceVolume
{
	// the voice sead, its child sound and the RootSound behind a voice's callback pointer (three reads); the RootSound's address
	public static bool ReadTerms(MemoryRead read, nint callback, Span<byte> sead, Span<byte> child, Span<byte> root, out nint rootPointer)
	{
		rootPointer = 0;
		if (callback == 0 || !read(callback - GameAudio.VoiceSeadBack, sead))
		{
			return false;
		}

		nint childPointer = (nint)BinaryPrimitives.ReadInt64LittleEndian(sead[GameAudio.WrapperOffset..]);
		if (childPointer == 0 || !read(childPointer, child))
		{
			return false;
		}

		rootPointer = (nint)BinaryPrimitives.ReadInt64LittleEndian(child[GameAudio.RootOffset..]);
		return rootPointer != 0 && read(rootPointer, root);
	}

	// the voice's volume with the bus's first fader (slider, mutes) replaced by the level; gameFader keeps the game's own
	public static float StreamVolume(VolumeCache cache, ReadOnlySpan<byte> sead, ReadOnlySpan<byte> child, ReadOnlySpan<byte> root, float level, bool gameFader = false)
	{
		int busId = root[GameAudio.RootBusId];
		ReadOnlySpan<byte> bus = cache.Bus(busId);
		if (bus.IsEmpty)
		{
			return 0f;
		}

		uint flags = BinaryPrimitives.ReadUInt32LittleEndian(root[GameAudio.RootExtendedFlags..]);
		int category = BinaryPrimitives.ReadInt32LittleEndian(root[GameAudio.RootPlayerCategory..]);
		float scalar = cache.HasGlobals
			? VolumeChain.Scalar(flags, category, cache.Mode, cache.Player, cache.Party, cache.Other)
			: 1f;
		SoundTerms terms = SoundTerms.From(sead, child, root, scalar);
		return VolumeChain.Compose(
			terms,
			gameFader ? VolumeChain.Fader(bus, GameAudio.BusVolume) : StreamFader(cache, level, busId),
			VolumeChain.Fader(bus, GameAudio.BusVolume2),
			VolumeChain.Fader(bus, GameAudio.BusVolume3),
			VolumeChain.Fader(bus, GameAudio.BusVolume4));
	}

	private static float StreamFader(VolumeCache cache, float level, int busId)
	{
		if (!cache.HasManager || busId is < 1 or > PeriodSlot.Buses)
		{
			return level;
		}

		ReadOnlySpan<byte> m = cache.Manager;
		int at = 4 * busId;
		return VolumeChain.Fader1(
			level,
			BinaryPrimitives.ReadSingleLittleEndian(m[(GameAudio.ManagerFactor2 + at)..]),
			BinaryPrimitives.ReadSingleLittleEndian(m[(GameAudio.ManagerFactor3 + at)..]),
			BinaryPrimitives.ReadSingleLittleEndian(m[(GameAudio.ManagerFactor4 + at)..]),
			BinaryPrimitives.ReadSingleLittleEndian(m[(GameAudio.ManagerFactor5 + at)..]));
	}
}
