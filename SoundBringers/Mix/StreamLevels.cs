using System;
using System.Threading;

using SoundBringers.Dsp;

namespace SoundBringers.Mix;

// The levels as one immutable snapshot: the window publishes, the audio thread reads Current once per period
public sealed class StreamLevels
{
	private Snapshot current = new([1f, 1f, 1f, 1f, 1f, 1f, 1f, 0f], 1f, false);
	private volatile bool background;

	public Snapshot Current => Volatile.Read(ref this.current);

	// the game window is inactive (set from the framework thread)
	public bool Background
	{
		get => this.background;
		set => this.background = value;
	}

	// levels / muted: BGM, Sound Effects, Voice, System, Ambient, Performance
	public void Publish(float master, bool masterMuted, ReadOnlySpan<float> levels, ReadOnlySpan<bool> muted, bool muteInBackground)
	{
		float[] categories = new float[8];
		for (int i = 0; i < 6; i++)
		{
			categories[i] = i < levels.Length && !(i < muted.Length && muted[i]) ? Math.Clamp(levels[i], 0f, 1f) : 0f;
		}

		categories[(int)StreamCategory.MasterOnly] = 1f;
		categories[(int)StreamCategory.Excluded] = 0f;
		Volatile.Write(ref this.current, new Snapshot(categories, masterMuted ? 0f : Math.Clamp(master, 0f, 1f), muteInBackground));
	}

	public sealed class Snapshot(float[] categories, float master, bool muteInBackground)
	{
		// the stream master, 0 when muted
		public float Master => master;

		public bool MuteInBackground => muteInBackground;

		// the category's level, 0 when muted (and always 0 for Excluded)
		public float Level(StreamCategory category) => categories[(int)category];
	}
}
