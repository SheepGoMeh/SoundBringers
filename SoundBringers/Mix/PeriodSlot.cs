using System;
using System.Threading;

using SoundBringers.Dsp;

namespace SoundBringers.Mix;

// one voice's copy: where its bytes sit in the slot's arena
internal struct VoiceRecord
{
	public nint Voice;
	public nint Sound; // the RootSound pointer
	public int VoiceOffset; // the voice object copy (PeriodSlot.VoiceBytes)
	public int SeadOffset;
	public int ChildOffset;
	public int RootOffset;
	public bool StepOk; // the step is usable; the buffers were copied only then
	public int Head; // the queue before the call
	public int Count;
	public int Read;
	public int Extras; // buffers the game's buffer-end callback queued during the render, copied
	public bool HasFilter;
	public int FilterOffset; // the filter object copy, FilterLength 0 when it could not be read
	public int FilterLength;
}

// One render period as the audio thread copied it: raw bytes in a preallocated arena, and the per-voice records
internal sealed class PeriodSlot(int arenaBytes, int maxVoices, int maxEffects)
{
	public const int VoiceBytes = 0xA48;
	public const int Buses = 20;
	public const int ManagerBytes = 0x1E0;
	private const int Align = 16;

	public readonly byte[] Arena = new byte[arenaBytes];
	public readonly VoiceRecord[] Voices = new VoiceRecord[maxVoices];
	public readonly int[] BufferOffsets = new int[maxVoices * 4]; // per voice: its two queue entries, then up to two refill extras
	public readonly int[] BufferLengths = new int[maxVoices * 4];
	public readonly (int Offset, int Length)[] Effects = new (int, int)[maxEffects];
	public readonly int[] BusOffsets = new int[Buses]; // each bus's block, -1 when not copied
	public readonly nint[] BusPointers = new nint[Buses];

	public int Used;
	public int VoiceCount;
	public int EffectCount;
	public int BusCount;

	// null: a silent period of Frames frames
	public ShadowGraph? Graph;
	public int Frames;
	public StreamLevels.Snapshot? Levels;
	public int Rate;
	public bool HasManager;
	public int ManagerOffset;
	public bool HasGlobals;
	public int Mode;
	public float Player;
	public float Party;
	public float Other;
	public long GapsBefore; // the mixer's dropped period count at publish

	public void Reset()
	{
		this.Used = 0;
		this.VoiceCount = 0;
		this.EffectCount = 0;
		this.BusCount = 0;
		this.Graph = null;
		this.Frames = 0;
		this.Levels = null;
		this.Rate = 0;
		this.HasManager = false;
		this.ManagerOffset = -1;
		this.HasGlobals = false;
		this.Mode = 0;
		this.Player = 0f;
		this.Party = 0f;
		this.Other = 0f;
		this.GapsBefore = 0;
		Array.Fill(this.BusOffsets, -1);
	}

	public int VoiceCapacity => this.Voices.Length;

	public int EffectCapacity => this.Effects.Length;

	// the next bytes of the arena (16-aligned); false when they do not fit
	public bool TryReserve(int bytes, out int offset)
	{
		offset = (this.Used + (Align - 1)) & ~(Align - 1);
		if (bytes < 0 || offset > this.Arena.Length - bytes)
		{
			return false;
		}

		this.Used = offset + bytes;
		return true;
	}

	public Span<byte> At(int offset, int length) => this.Arena.AsSpan(offset, length);
}

// Slots from one producer (the audio thread) to one consumer (the worker); never blocks, locks or allocates
internal sealed class SlotQueue(int count, int arenaBytes, int maxVoices, int maxEffects)
{
	private readonly PeriodSlot[] slots = Fill(count, arenaBytes, maxVoices, maxEffects);
	private long head; // slots published; written by the producer
	private long tail; // slots released; written by the consumer

	private static PeriodSlot[] Fill(int count, int arenaBytes, int maxVoices, int maxEffects)
	{
		PeriodSlot[] slots = new PeriodSlot[count];
		for (int i = 0; i < count; i++)
		{
			slots[i] = new PeriodSlot(arenaBytes, maxVoices, maxEffects);
		}

		return slots;
	}

	// producer: the next free slot, reset; null when none is free
	public PeriodSlot? TryBegin()
	{
		if (this.head - Volatile.Read(ref this.tail) >= this.slots.Length)
		{
			return null;
		}

		PeriodSlot slot = this.slots[this.head % this.slots.Length];
		slot.Reset();
		return slot;
	}

	// producer: the slot TryBegin returned is complete
	public void Publish() => Volatile.Write(ref this.head, this.head + 1);

	// consumer: the oldest published slot, null when there is none
	public PeriodSlot? Peek() =>
		this.tail < Volatile.Read(ref this.head) ? this.slots[this.tail % this.slots.Length] : null;

	// consumer: done with the slot Peek returned
	public void Release() => Volatile.Write(ref this.tail, this.tail + 1);
}
