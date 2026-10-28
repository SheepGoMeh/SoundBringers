namespace SoundBringers.Dsp;

public enum StreamCategory
{
	Bgm,
	SoundEffects,
	Voice,
	System,
	Ambient,
	Performance,
	MasterOnly,
	Excluded,
}

// the game's bus ids (1..20) grouped by the settings sliders; Movie is under the stream master only, Vibration is left out
public static class Categories
{
	public static StreamCategory Of(int busId) => busId switch
	{
		1 or 8 or 9 or 16 => StreamCategory.Bgm,
		2 or 6 or 7 or 10 => StreamCategory.SoundEffects,
		3 => StreamCategory.Voice,
		4 => StreamCategory.System,
		5 or 11 or 12 or 14 or 15 or 19 or 20 => StreamCategory.Ambient,
		17 => StreamCategory.Performance,
		13 => StreamCategory.MasterOnly,
		_ => StreamCategory.Excluded,
	};
}
