using System;

namespace SoundBringers.Dsp;

// the game's low-pass core filter (784-byte object, process sub_141EC8020): one biquad stage at +0xD0
public sealed partial class LowpassEffect
{
	public const int Size = 0x310;
	private const int StageOffset = 0xD0;

	private BiquadState stage;

	public ReadOnlySpan<float> State => ((ReadOnlySpan<float>)this.stage)[Biquad.CoeffFloats..];

	// coefficients always, filter memory with withState
	public void Load(ReadOnlySpan<byte> core, bool withState) => Biquad.Load(core[StageOffset..], this.stage, withState);

	public void Process(Span<float> buf, int frames, int channels)
	{
		Span<float> floats = this.stage;
		Biquad.ProcessDf1(buf, frames, channels, floats[..Biquad.CoeffFloats], floats[Biquad.CoeffFloats..]);
	}
}
