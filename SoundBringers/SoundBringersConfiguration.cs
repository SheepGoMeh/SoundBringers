using Dalamud.Configuration;

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

	public void Save() => Service.PluginInterface.SavePluginConfig(this);
}
