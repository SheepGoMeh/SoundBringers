using System;

namespace SoundBringers.Dsp;

public delegate void EffectProcess(Span<float> buffer, int frames, int channels);

// reads game memory; false when the whole span could not be read
public delegate bool MemoryRead(nint address, Span<byte> destination);

// the audio thread's half of a mirror: copies the effect's raw bytes into the blob, returns the length, -1 when unreadable
public delegate int EffectCopy(Span<byte> blob);

// the worker's half: takes the coefficients from a copy; false (and nothing changed) when they are not usable. skip is the game's bypass flag.
public delegate bool EffectLoad(ReadOnlySpan<byte> blob, out bool skip);

// the ICoreEffect bypass flag (vf2) sits at object +0x10 in every effect class
public static class EffectLayout
{
	public const int Skip = 0x10;
}

// one game effect on a submix or master as the shadow graph runs it. Built on the framework thread; copy runs on the audio thread
// (it touches no effect state), load and process only on the worker.
public sealed class BusEffect(nint pointer, bool tail, EffectProcess process, int copyBytes = 0, EffectCopy? copy = null, EffectLoad? load = null)
{
	// the game's ICoreEffect pointer (the object + 8)
	public nint Pointer => pointer;

	// the effect keeps producing output with no input (the game's vf3: FaithReverb only)
	public bool Tail => tail;

	// the most bytes Copy writes
	public int CopyBytes => copyBytes;

	// the game's bypass flag (vf2), refreshed with every load
	public bool Skip;

	// false until the first successful load: an effect with no coefficients stays out of the path
	public bool Ready { get; private set; }

	// audio thread: 0 when the effect has nothing to copy, -1 when the game's memory could not be read
	public int Copy(Span<byte> blob) => copy == null ? 0 : copy(blob);

	// worker: an empty blob is a copy that failed
	public void Load(ReadOnlySpan<byte> blob)
	{
		if (load == null)
		{
			this.Ready = true;
		}
		else if (!blob.IsEmpty && load(blob, out bool bypass))
		{
			this.Skip = bypass;
			this.Ready = true;
		}
	}

	public void Process(Span<float> buffer, int frames, int channels) => process(buffer, frames, channels);
}
