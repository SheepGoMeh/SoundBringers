using System;
using System.Runtime.InteropServices;

namespace SoundBringers.Dsp;

// the game's Stringendo core effect (TimeStretchBGM only, 0x20060-byte object, process sub_141EAD5E0): a two-tap pitch shifter.
// The input goes into a 32,768-float ring (eight floats per frame whatever the channel count), two read taps move by
// 1 - 1/ratio frames per frame, are interpolated linearly between neighbouring frames and cross-faded by a window of a phase
// that advances 1/6300 per frame. A tap snaps back to 0 when the phase passes 0.25 (tap A) or 0.75 (tap B), once per cycle, when
// its own weight is 0. Offsets below are from the object start (the game's process sees the object + 8, hence the decompile's smaller
// numbers); no flush. The ring and its write index stay the plugin's; the ratio fade, phase, taps and flags are mirrored.
public sealed partial class StringendoEffect
{
	public const int Size = 0x20060;
	public const int RingFloats = 0x8000;
	public const int TailOffset = 0x20018; // the write index; the ratio DynamicValue<2> follows at +8
	public const int TailBytes = Size - TailOffset;
	private const int Stride = 8; // floats per ring frame
	private const float PhaseStep = 0.00015873015f; // 1 / 6300 (dword_142546860)

	private const int RatioInTail = 8;
	private const int PhaseInTail = 0x38;
	private const int TapAInTail = 0x3C;
	private const int TapBInTail = 0x40;
	private const int FlagAInTail = 0x44;

	private readonly float[] ring = new float[RingFloats];
	private readonly FaithReverbEffect.Fade ratio = new();

	// the game's constructor starts the ratio at 1
	public StringendoEffect()
	{
		Span<byte> tail = stackalloc byte[TailBytes];
		BitConverter.GetBytes(1f).CopyTo(tail[(RatioInTail + 12)..]);
		this.Load(tail);
	}

	public int WriteIndex { get; private set; }

	public float Phase { get; private set; }

	public float TapA { get; private set; }

	public float TapB { get; private set; }

	public bool FlagA { get; private set; }

	public bool FlagB { get; private set; }

	public ReadOnlySpan<float> Ring => this.ring;

	// the object's last TailBytes bytes: ratio, phase, taps, flags (never the ring or the write index)
	public void Load(ReadOnlySpan<byte> tail)
	{
		this.ratio.Read(tail, RatioInTail);
		this.Phase = BitConverter.ToSingle(tail[PhaseInTail..]);
		this.TapA = BitConverter.ToSingle(tail[TapAInTail..]);
		this.TapB = BitConverter.ToSingle(tail[TapBInTail..]);
		this.FlagA = tail[FlagAInTail] != 0;
		this.FlagB = tail[FlagAInTail + 1] != 0;
	}

	// sqrt(1 - |1 - |1 - |2 x|||), sub_141EADC10 (three fabs and a square root)
	private static float Window(float x)
	{
		return MathF.Sqrt(1f - MathF.Abs(1f - MathF.Abs(1f - MathF.Abs(x + x))));
	}

	// the game reads past the ring for extreme ratios and for ratio < 1 (negative taps); this wraps instead
	private static int Wrap(int index)
	{
		return index & (RingFloats - 1);
	}

	// the ring position of a tap's newer sample and the one before it (the game's v16 / v56 and v19 / v20)
	private int Position(float whole, out int older)
	{
		int newer = Wrap(this.WriteIndex - (Stride * (int)whole));
		older = Wrap(newer - Stride);
		return newer;
	}

	// any channel count above 0 (the game loops them all); more than eight overlap the next frame's slots, as in the game
	public void Process(Span<float> buf, int frames, int channels)
	{
		if (channels < 1)
		{
			return;
		}

		float step = 1f - (1f / this.ratio.Value());
		for (int f = 0; f < frames; f++)
		{
			this.Phase += PhaseStep;
			if (this.Phase > 1f)
			{
				this.Phase -= 1f;
			}

			float phase = this.Phase;
			if (phase >= 0.25f)
			{
				if (!this.FlagA && phase > 0.25f)
				{
					this.TapA = 0f;
					this.FlagA = true;
				}
			}
			else
			{
				this.FlagA = false;
				this.FlagB = false;
			}

			if (!this.FlagB && phase > 0.75f)
			{
				this.TapB = 0f;
				this.FlagB = true;
			}

			this.TapA += step;
			this.TapB += step;
			float wholeA = MathF.Truncate(this.TapA);
			float fractionA = this.TapA - wholeA;
			float wholeB = MathF.Truncate(this.TapB);
			float fractionB = this.TapB - wholeB;
			int newerA = this.Position(wholeA, out int olderA);
			int newerB = this.Position(wholeB, out int olderB);
			float weightB = Window(phase - 0.25f);
			float weightA = Window(phase + 0.25f);
			float inverseA = 1f - fractionA;
			float inverseB = 1f - fractionB;
			Span<float> frame = buf.Slice(f * channels, channels);
			for (int c = 0; c < channels; c++)
			{
				this.ring[Wrap(this.WriteIndex + c)] = frame[c];
				frame[c] = (((fractionA * this.ring[Wrap(olderA + c)]) + (inverseA * this.ring[Wrap(newerA + c)])) * weightA)
					+ (((inverseB * this.ring[Wrap(newerB + c)]) + (fractionB * this.ring[Wrap(olderB + c)])) * weightB);
			}

			this.WriteIndex = (this.WriteIndex + Stride) % RingFloats;
		}
	}
}
