using System;

namespace SoundBringers.Dsp;

// SQEX::Sd::Driver::Equalizer (80-byte object, band count +64, band array pointer +72, process sub_141EA9EB0):
// cascaded biquad stages, 576 bytes each, behind the pointer
public sealed partial class EqualizerEffect
{
	public const int Size = 0x50;
	public const int CountOffset = 64;
	public const int BandsOffset = 72;
	public const int MaxBands = 8;

	private readonly BiquadState[] stages = new BiquadState[MaxBands]; // fixed capacity: no allocation per period
	private int count;

	public int Bands => this.count;

	// filter memory of every stage, concatenated
	public ReadOnlySpan<float> State
	{
		get
		{
			float[] all = new float[this.count * Biquad.StateFloats];
			for (int i = 0; i < this.count; i++)
			{
				((ReadOnlySpan<float>)this.stages[i])[Biquad.CoeffFloats..].CopyTo(all.AsSpan(i * Biquad.StateFloats));
			}

			return all;
		}
	}

	// the band array as in game memory
	public void Load(ReadOnlySpan<byte> bands, bool withState)
	{
		this.count = Math.Min(bands.Length / Biquad.StageBytes, MaxBands);
		for (int i = 0; i < this.count; i++)
		{
			Biquad.Load(bands[(i * Biquad.StageBytes)..], this.stages[i], withState);
		}
	}

	// the game's 8-channel path always runs 4 stages and ignores the count; the 2 and 16 channel paths loop over it.
	// (its final multiply by 1.0f is an identity and is omitted)
	public void Process(Span<float> buf, int frames, int channels) =>
		Biquad.ProcessCascade(this.stages.AsSpan(0, channels == 8 ? 4 : this.count), buf, frames, channels);
}
