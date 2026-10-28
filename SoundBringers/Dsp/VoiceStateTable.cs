using System;

namespace SoundBringers.Dsp;

// Per-voice state the shadow keeps between periods (the stream volume it ramped to, the voice's one-pole filter), in a fixed-capacity
// table so nothing allocates per period. Keyed by voice pointer; an entry starts fresh when the voice's sound (RootSound pointer)
// changes, and the oldest entry in reach is taken over by a voice that has none.
public sealed class VoiceStateTable
{
	private const int Probe = 16;

	private readonly Slot[] slots;
	private long period;

	public VoiceStateTable(int capacity = 512)
	{
		this.slots = new Slot[capacity];
		for (int i = 0; i < capacity; i++)
		{
			this.slots[i].Filter = new VoiceFilter();
		}
	}

	public void BeginPeriod() => this.period++;

	// the volume the shadow ramps from this period. A voice seen for the first time has no history: the game commits previous = target
	// while a voice is not rendering, so its first rendered period does not ramp, and neither does the shadow (current is returned).
	public float PreviousVolume(nint voice, nint sound, float current)
	{
		ref Slot slot = ref this.Find(voice, sound);
		return slot.Fresh ? current : slot.Volume;
	}

	// What the voice was rendered with last period: Known only when it was rendered in the period just before this one (a voice
	// that was gated or paused has no continuity to keep).
	public History Last(nint voice, nint sound)
	{
		ref Slot slot = ref this.Find(voice, sound);
		return new History(!slot.Fresh && slot.Stored == this.period - 1, slot.RatioPath, slot.Ratio, slot.Volume);
	}

	// volume: what the voice was actually rendered with (end of the period); ratioPath / ratio: how it was reached (see VolumeFollow)
	public void Store(nint voice, nint sound, float volume, bool ratioPath = false, float ratio = 1f)
	{
		ref Slot slot = ref this.Find(voice, sound);
		slot.Volume = volume;
		slot.Fresh = false;
		slot.Stored = this.period;
		slot.RatioPath = ratioPath;
		slot.Ratio = ratio;
	}

	public readonly record struct History(bool Known, bool LastWasRatio, float LastRatio, float LastVolume);

	public VoiceFilter Filter(nint voice, nint sound) => this.Find(voice, sound).Filter!;

	private ref Slot Find(nint voice, nint sound)
	{
		int start = (int)(((ulong)voice >> 4) * 0x9E3779B1UL % (ulong)this.slots.Length);
		int victim = start;
		for (int i = 0; i < Probe; i++)
		{
			int at = (start + i) % this.slots.Length;
			ref Slot slot = ref this.slots[at];
			if (slot.Voice == voice)
			{
				if (slot.Sound != sound)
				{
					Claim(ref slot, voice, sound);
				}

				slot.Seen = this.period;
				return ref slot;
			}

			if (slot.Seen < this.slots[victim].Seen)
			{
				victim = at;
			}
		}

		// not present: take the oldest slot in reach (an unused slot has Seen 0 and goes first)
		ref Slot taken = ref this.slots[victim];
		Claim(ref taken, voice, sound);
		taken.Seen = this.period;
		return ref taken;
	}

	private static void Claim(ref Slot slot, nint voice, nint sound)
	{
		slot.Voice = voice;
		slot.Sound = sound;
		slot.Volume = 0f;
		slot.Fresh = true;
		slot.Stored = 0;
		slot.RatioPath = false;
		slot.Ratio = 1f;
		slot.Filter!.Reset();
	}

	private struct Slot
	{
		public nint Voice;
		public nint Sound;
		public float Volume;
		public bool Fresh;
		public long Stored;
		public bool RatioPath;
		public float Ratio;
		public long Seen;
		public VoiceFilter? Filter;
	}
}
