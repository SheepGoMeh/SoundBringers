using System;
using System.Buffers.Binary;

namespace SoundBringers.Dsp;

// the user equalizer's core effect on the masters (2,992-byte object, vtable 0x14254D180, process sub_141ECA290):
// four biquad stages inline at +0x2A0, then a gain float at +0xBA0
public sealed partial class MasterEqualizerEffect
{
	public const int Size = 0xBB0;
	private const int StagesOffset = 0x2A0;
	private const int GainOffset = 0xBA0;
	private const int StageCount = 4;

	private readonly BiquadState[] stages = new BiquadState[StageCount];
	private float gain = 1f;

	// filter memory of every stage, concatenated
	public ReadOnlySpan<float> State
	{
		get
		{
			float[] all = new float[StageCount * Biquad.StateFloats];
			for (int i = 0; i < StageCount; i++)
			{
				((ReadOnlySpan<float>)this.stages[i])[Biquad.CoeffFloats..].CopyTo(all.AsSpan(i * Biquad.StateFloats));
			}

			return all;
		}
	}

	// coefficients and gain always, filter memory with withState
	public void Load(ReadOnlySpan<byte> core, bool withState)
	{
		for (int i = 0; i < StageCount; i++)
		{
			Biquad.Load(core[(StagesOffset + (i * Biquad.StageBytes))..], this.stages[i], withState);
		}

		this.gain = BinaryPrimitives.ReadSingleLittleEndian(core[GainOffset..]);
	}

	public void Process(Span<float> buf, int frames, int channels)
	{
		Biquad.ProcessCascade(this.stages, buf, frames, channels);
		if (channels is 2 or 8 or 16)
		{
			for (int i = 0; i < frames * channels; i++)
			{
				buf[i] *= this.gain;
			}
		}
	}
}
