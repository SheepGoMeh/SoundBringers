using System;
using System.Runtime.InteropServices;

namespace SoundBringers.Dsp;

// the game's wah-wah core effect (2192-byte object, process sub_141EC7040): parameters at +416, three band-pass biquad stages
// (576 bytes each, as Biquad) at +464, +1040 and +1616. Parameters: [0] base frequency, [1] bandwidth, [2]/[4] LFO rates,
// [3]/[5] LFO depths, [6] dry, [7] wet, [8]/[9] LFO phases (advanced per frame, wrapped at pi).
public sealed partial class WahWahEffect
{
	public const int Size = 0x890;
	private const int ParamsOffset = 416;
	private const int StagesOffset = 464;
	private const int StageCount = 3;
	private const int ParamCount = 10;
	private const float Pi = 3.1415927f;
	private const float TwoPi = 6.2831855f;
	private const float PhaseStep = 2.0943952f; // 2 pi / 3, between the three stages (dword_14214DE1C)

	private readonly float[] parameters = new float[ParamCount];
	private readonly BiquadState[] stages = new BiquadState[StageCount];

	public ReadOnlySpan<float> Phases => this.parameters.AsSpan(8, 2);

	// filter memory of the three stages, concatenated
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

	// parameters and phases always, filter memory with withState (coefficients are recomputed every frame)
	public void Load(ReadOnlySpan<byte> core, bool withState)
	{
		MemoryMarshal.Cast<byte, float>(core.Slice(ParamsOffset, ParamCount * sizeof(float))).CopyTo(this.parameters);
		if (withState)
		{
			for (int i = 0; i < StageCount; i++)
			{
				Biquad.Load(core[(StagesOffset + (i * Biquad.StageBytes))..], this.stages[i], true);
			}
		}
	}

	// band-pass coefficients as sub_141EC86C0 (the same in every lane)
	private static void SetCoefficients(Span<float> stage, float frequency, float bandwidth)
	{
		float w = frequency * TwoPi;
		float alpha = MathF.Sin(w) * (bandwidth * 0.5f);
		float inverse = 1f / (alpha + 1f);
		stage[..Biquad.Lanes].Fill((MathF.Cos(w) + MathF.Cos(w)) * inverse);
		stage.Slice(Biquad.Lanes, Biquad.Lanes).Fill((alpha - 1f) * inverse);
		float b0 = inverse * alpha;
		stage.Slice(2 * Biquad.Lanes, Biquad.Lanes).Fill(b0);
		stage.Slice(3 * Biquad.Lanes, Biquad.Lanes).Fill(0f);
		stage.Slice(4 * Biquad.Lanes, Biquad.Lanes).Fill(-b0);
	}

	// the game loops any lane count; only counts outside 1 to 16 are left alone
	public void Process(Span<float> buf, int frames, int channels)
	{
		if (channels is < 1 or > Biquad.Lanes)
		{
			return;
		}

		float[] p = this.parameters;
		float logBase = MathF.Log(1f / p[0]);
		float depth0 = logBase * p[3];
		float depth1 = logBase * p[5];
		Span<float> bands = stackalloc float[StageCount * Biquad.Lanes];
		for (int f = 0; f < frames; f++)
		{
			float phase = p[8];
			for (int s = 0; s < StageCount; s++)
			{
				float octaves = (MathF.Sin(p[9]) * depth1) + (MathF.Sin(phase) * depth0);
				SetCoefficients(this.stages[s], MathF.Pow(2f, octaves) * p[0], p[1]);
				phase += PhaseStep;
				if (phase > Pi)
				{
					phase += -TwoPi;
				}
			}

			Span<float> frame = buf.Slice(f * channels, channels);
			for (int s = 0; s < StageCount; s++)
			{
				Span<float> band = bands.Slice(s * Biquad.Lanes, channels);
				frame.CopyTo(band);
				Span<float> floats = this.stages[s];
				Biquad.ProcessDf1(band, 1, channels, floats[..Biquad.CoeffFloats], floats[Biquad.CoeffFloats..], flushOutput: false, anyChannelCount: true);
			}

			for (int c = 0; c < channels; c++)
			{
				float sum = ((bands[c] + 0f) + bands[Biquad.Lanes + c]) + bands[(2 * Biquad.Lanes) + c];
				frame[c] = (sum * p[7]) + (frame[c] * p[6]);
			}

			p[8] += p[2];
			if (p[8] > Pi)
			{
				p[8] -= TwoPi;
			}

			p[9] += p[4];
			if (p[9] > Pi)
			{
				p[9] -= TwoPi;
			}
		}
	}
}
