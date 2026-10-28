using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SoundBringers.Dsp;

// one biquad stage as the game lays it out (576 bytes): coefficient vectors (y1, y2, x0, x1, x2) then filter memory
// vectors (x1, x2, y1, y2); each vector holds 16 lanes, one per channel
[InlineArray(Biquad.StageFloats)]
public struct BiquadState
{
	private float element;
}

public static partial class Biquad
{
	public const int Lanes = 16;
	public const int CoeffFloats = 5 * Lanes;
	public const int StateFloats = 4 * Lanes;
	public const int StageFloats = CoeffFloats + StateFloats;
	public const int StageBytes = StageFloats * sizeof(float);

	private const float Denormal = 1e-15f; // dword_14211DD50 (0x26901D7D), the constant every biquad path in the game reads

	public static float Flush(float x) => (x + Denormal) - Denormal;

	// direct form I, in place, as the game's 2/8/16 channel loops (other channel counts are left alone):
	// y = ((x*b0 + x1*b1) + x2*b2) + y1*a1 + y2*a2, flushed. The game's stereo equalizer paths subtract the two feedback terms instead.
	public static void ProcessDf1(
		Span<float> buffer,
		int frames,
		int channels,
		ReadOnlySpan<float> coeffs,
		Span<float> state,
		bool flushInput = false,
		bool subtractFeedback = false,
		bool flushOutput = true,
		bool anyChannelCount = false)
	{
		if (anyChannelCount ? channels is < 1 or > Lanes : channels is not (2 or 8 or 16))
		{
			return;
		}

		for (int f = 0; f < frames; f++)
		{
			for (int c = 0; c < channels; c++)
			{
				float x = buffer[(f * channels) + c];
				if (flushInput)
				{
					x = Flush(x);
				}

				float y = (state[c] * coeffs[(3 * Lanes) + c]) + (x * coeffs[(2 * Lanes) + c]);
				y = (state[Lanes + c] * coeffs[(4 * Lanes) + c]) + y;
				float feedback1 = state[(2 * Lanes) + c] * coeffs[c];
				float feedback2 = state[(3 * Lanes) + c] * coeffs[Lanes + c];
				y = subtractFeedback ? (y - feedback1) - feedback2 : (feedback1 + y) + feedback2;
				if (flushOutput)
				{
					y = Flush(y);
				}
				state[Lanes + c] = state[c];
				state[c] = x;
				state[(3 * Lanes) + c] = state[(2 * Lanes) + c];
				state[(2 * Lanes) + c] = y;
				buffer[(f * channels) + c] = y;
			}
		}
	}

	// the stages in order, input flushed at each; stages do not interact, so one pass per stage equals the game's per-frame cascade
	public static void ProcessCascade(Span<BiquadState> stages, Span<float> buffer, int frames, int channels)
	{
		foreach (ref BiquadState stage in stages)
		{
			Span<float> floats = stage;
			ProcessDf1(buffer, frames, channels, floats[..CoeffFloats], floats[CoeffFloats..], true, channels == 2);
		}
	}

	// copies a stage from memory laid out as the game's: coefficients always, filter memory only when asked
	public static void Load(ReadOnlySpan<byte> memory, Span<float> stage, bool withState) =>
		MemoryMarshal.Cast<byte, float>(memory[..(withState ? StageBytes : CoeffFloats * sizeof(float))]).CopyTo(stage);
}
