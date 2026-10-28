using System;
using System.Buffers.Binary;

namespace SoundBringers.Dsp;

// the game's per-voice one-pole filter (208-byte object): y = x * b + y * a per channel, in place
public sealed partial class VoiceFilter
{
	private const float Denormal = 1e-15f;
	private const float MixerRate = 48000f;

	private readonly float[] state = new float[16];
	private bool skip; // +16: the game bypasses the effect while set
	private float a; // +80
	private float b = 1f; // +84

	public ReadOnlySpan<float> State => this.state;

	public float A => this.a;

	public float B => this.b;

	// type (+72): 0 = off, 1 = low-pass, 2 = high-pass-like; coefficients as sub_141ECAE40, other types leave them unchanged
	public void SetParameters(float cutoff, int type)
	{
		// the game reads the rate from sub_141E9DF40; it is always 48 kHz
		float w = cutoff * 6.2831855f * (1f / MixerRate);
		switch (type)
		{
			case 0:
				this.a = 0f;
				this.b = 1f;
				break;
			case 1:
				this.a = MathF.Exp(-w);
				this.b = 1f - this.a;
				break;
			case 2:
				this.a = MathF.Exp(w - 3.1415927f);
				this.b = 1f - this.a;
				break;
		}
	}

	// the game only handles 2, 8 and 16 channels
	public void Process(Span<float> buffer, int frames, int channels)
	{
		if (this.skip || channels is not (2 or 8 or 16))
		{
			return;
		}

		for (int f = 0; f < frames; f++)
		{
			for (int c = 0; c < channels; c++)
			{
				float x = ((buffer[(f * channels) + c] + Denormal) - Denormal) * this.b;
				float y = x + (this.state[c] * this.a);
				y = (y + Denormal) - Denormal;
				this.state[c] = y;
				buffer[(f * channels) + c] = y;
			}
		}
	}

	// a new sound on this voice: no memory, no coefficients until the next mirror
	public void Reset()
	{
		this.state.AsSpan().Clear();
		this.skip = false;
		this.a = 0f;
		this.b = 1f;
	}
}
