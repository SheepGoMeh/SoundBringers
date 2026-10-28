using System;

namespace SoundBringers.Dsp;

// How the stream's voice volume follows the game's. The game renders a voice with the volume it committed (+0x530, previous +0x938),
// which trails the sound's volume chain by a few ticks whenever anything fades. Scaling the committed volumes by stream / game (both
// recomputed now) keeps that lag, so the stream matches the game exactly. The ratio is only valid while it does not change over the
// lag: it can change with the plugin's own levels (the committed volumes carry no plugin level, so the ratio is applied fresh and the
// ramp starts from what was rendered), but not with the game's own first fader (slider, mutes, background mute), which the committed
// volumes still hold the old value of. While that fader is moving, or just moved, the recomputed stream volume is used.
public static class VolumeFollow
{
	public readonly record struct Follow(float Volume, float Previous, bool RatioPath, float Ratio);

	// composed: stream volume recomputed now; game: the game's volume recomputed now (0 when muted in game); committed / committedPrevious:
	// the voice's +0x530 / +0x938; settled: the bus's first fader has been steady; last: what the voice was rendered with last period.
	// Both paths ramp from what the stream actually rendered last period; a voice with no history does not ramp (as in the game).
	public static Follow Choose(
		float composed,
		float game,
		float committed,
		float committedPrevious,
		bool settled,
		VoiceStateTable.History last)
	{
		float ratio = game > 0f ? composed / game : float.NaN;
		if (float.IsFinite(ratio) && float.IsFinite(composed) && float.IsFinite(game) && settled)
		{
			float volume = committed * ratio;
			float previous = !last.Known
				? committedPrevious * ratio
				: last.LastWasRatio ? committedPrevious * last.LastRatio : last.LastVolume;
			if (float.IsFinite(volume) && float.IsFinite(previous))
			{
				return new Follow(volume, previous, true, ratio);
			}
		}

		return new Follow(composed, last.Known ? last.LastVolume : composed, false, 1f);
	}
}

// Whether a bus's first fader (slider, mutes, background mute) has been steady: no fade steps left and the same value for Steady
// consecutive observations.
public sealed class FaderTracker
{
	public const int Steady = 8;
	private const int Buses = 21;

	private readonly float[] values = new float[Buses];
	private readonly int[] still = new int[Buses];

	public void Observe(int bus, float value, int fadeSteps)
	{
		if (bus is < 0 or >= Buses)
		{
			return;
		}

		this.still[bus] = fadeSteps == 0 && value == this.values[bus] ? Math.Min(this.still[bus] + 1, Steady) : 0;
		this.values[bus] = value;
	}

	public bool Settled(int bus) => bus is >= 0 and < Buses && this.still[bus] >= Steady;

	// a gap in the periods: nothing observed before it counts as steady
	public void Reset() => Array.Clear(this.still);
}
