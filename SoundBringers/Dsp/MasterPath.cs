using System;

namespace SoundBringers.Dsp;

public static class MasterPath
{
	// 8 -> 2 fold of the game's final conversion (sub_141EA2EF0, gain 1.0): rows are the mixer's FL, FR, FC, LFE, BL, BR, SL, SR
	// spans over static data, not static arrays: no type initializer to run on the worker
	private static ReadOnlySpan<float> Left => [0.89f, 0.11f, 0.58f, 0.385f, 0.92f, 0f, 0f, 0f];

	private static ReadOnlySpan<float> Right => [0.11f, 0.89f, 0.58f, 0.385f, 0f, 0.92f, 0f, 0f];

	public static void FoldTo2(ReadOnlySpan<float> in8, Span<float> out2, int frames)
	{
		ReadOnlySpan<float> left = Left;
		ReadOnlySpan<float> right = Right;
		for (int f = 0; f < frames; f++)
		{
			float l = 0f;
			float r = 0f;
			for (int c = 0; c < 8; c++)
			{
				float x = in8[(f * 8) + c];
				l += x * left[c];
				r += x * right[c];
			}

			out2[f * 2] = l;
			out2[(f * 2) + 1] = r;
		}
	}

	// the master process (sub_141EB5DE0): effects (not bypassed, when the master had input or the effect has a tail), then the gain.
	// The vector at master +0xE0 the game also multiplies by is 1.0 with spatial audio off. Returns whether it ran.
	public static bool ApplyMaster(Span<float> buffer, int frames, int channels, ReadOnlySpan<BusEffect> effects, int inputs, float gain)
	{
		bool ran = inputs > 0;
		foreach (BusEffect effect in effects)
		{
			if (effect.Ready && !effect.Skip && (inputs > 0 || effect.Tail))
			{
				effect.Process(buffer, frames, channels);
				ran = true;
			}
		}

		if (ran && gain != 1f)
		{
			Simd.Scale(buffer[..(frames * channels)], gain);
		}

		return ran;
	}

	// spatial audio off: the render callback adds Master ambisonics into Master bed at gain 1 (a MixIn of the bed master)
	public static void AddAmbisonics(Span<float> bed, ReadOnlySpan<float> ambisonics, int length)
	{
		Simd.Add(bed[..length], ambisonics);
	}
}
