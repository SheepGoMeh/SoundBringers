using System;

using Dalamud.Configuration;

using SoundBringers.Output;

namespace SoundBringers;

public class SoundBringersConfiguration: IPluginConfiguration
{
	public int Version { get; set; }

	public string? DeviceId;
	public float StreamMaster = 1f;
	public bool StreamMuted;
	public float[] Levels = [1f, 1f, 1f, 1f, 1f, 1f];
	public bool[] Muted = new bool[6];
	public bool MuteInBackground;
	public OutputMode OutputMode;
	public bool InlineMix;

	// a saved file can hold anything: six finite levels in 0..1, six mutes
	public void Validate()
	{
		float[] fixedLevels = [1f, 1f, 1f, 1f, 1f, 1f];
		for (int i = 0; i < fixedLevels.Length; i++)
		{
			if (this.Levels != null && i < this.Levels.Length && float.IsFinite(this.Levels[i]))
			{
				fixedLevels[i] = Math.Clamp(this.Levels[i], 0f, 1f);
			}
		}

		bool[] fixedMutes = new bool[6];
		for (int i = 0; i < fixedMutes.Length; i++)
		{
			fixedMutes[i] = this.Muted != null && i < this.Muted.Length && this.Muted[i];
		}

		this.Levels = fixedLevels;
		this.Muted = fixedMutes;
		this.StreamMaster = float.IsFinite(this.StreamMaster) ? Math.Clamp(this.StreamMaster, 0f, 1f) : 1f;
		if (!Enum.IsDefined(this.OutputMode) || StreamOutput.IsWine)
			this.OutputMode = OutputMode.Shared;
	}

	public void Save() => Service.PluginInterface.SavePluginConfig(this);
}
