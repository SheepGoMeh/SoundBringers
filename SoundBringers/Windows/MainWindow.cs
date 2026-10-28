using System;
using System.Collections.Generic;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

using SoundBringers.Mix;
using SoundBringers.Output;

namespace SoundBringers.Windows;

internal class MainWindow(SoundBringersConfiguration configuration, StreamOutput output, StreamLevels levels, Func<StreamState> state)
	: Window("SoundBringers", ImGuiWindowFlags.AlwaysAutoResize)
{
	private static readonly string[] CategoryNames = ["BGM", "Sound Effects", "Voice", "System Sounds", "Ambient Sounds", "Performance"];
	private static readonly string[] ModeNames = ["Shared", "Low latency", "Exclusive"];

	private IReadOnlyList<OutputDevice> devices = [];

	public override void OnOpen() => this.devices = output.Devices();

	public override void Draw()
	{
		if (ImGui.BeginCombo("Output", output.DeviceName ?? "(none)"))
		{
			foreach (OutputDevice device in this.devices)
			{
				if (ImGui.Selectable(device.Name, device.Id == configuration.DeviceId))
				{
					configuration.DeviceId = device.Id;
					configuration.Save();
					// the framework update starts the saved device within a second
					output.Stop();
				}
			}

			ImGui.EndCombo();
		}

		ImGui.SameLine();
		if (ImGui.Button("Refresh"))
			this.devices = output.Devices();

		if (!StreamOutput.IsWine)
		{
			int mode = (int)configuration.OutputMode;
			if (ImGui.Combo("Mode", ref mode, ModeNames, ModeNames.Length))
			{
				configuration.OutputMode = (OutputMode)mode;
				configuration.Save();
				output.Stop();
			}

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Low latency needs Windows 10 or later and falls back to shared when the device can't do it.\nExclusive is lowest, but nothing else can play to the device while the stream has it.");
			if (output.ExclusiveRefused)
				ImGui.TextDisabled("The device refused exclusive mode, using shared");
		}

		ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28);
		ImGui.TextDisabled(output.DeviceName == null ? StreamStatus.Text(state()) : $"{StreamStatus.Text(state())}: {output.DeviceName}");
		ImGui.PopTextWrapPos();

		ImGui.Separator();
		this.Level("Stream", ref configuration.StreamMaster, ref configuration.StreamMuted);
		ImGui.Separator();
		for (int i = 0; i < CategoryNames.Length; i++)
			this.Level(CategoryNames[i], ref configuration.Levels[i], ref configuration.Muted[i]);

		ImGui.Separator();
		if (ImGui.Checkbox("Mute while the game is in the background", ref configuration.MuteInBackground))
			this.Changed();

		ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28);
		ImGui.TextDisabled("Sounds the game drops because Self, Party or Other PCs is at or near 0 are missing from the stream too.");
		ImGui.PopTextWrapPos();
	}

	private void Level(string name, ref float level, ref bool muted)
	{
		if (ImGui.Checkbox($"##mute{name}", ref muted))
			this.Changed();
		if (ImGui.IsItemHovered())
			ImGui.SetTooltip("Mute");

		ImGui.SameLine();
		int percent = (int)MathF.Round(level * 100);
		if (ImGui.SliderInt(name, ref percent, 0, 100, "%d%%"))
		{
			level = percent / 100f;
			this.Publish();
		}

		// saved once the drag ends; the levels are live while it goes on
		if (ImGui.IsItemDeactivatedAfterEdit())
			configuration.Save();
	}

	private void Changed()
	{
		configuration.Save();
		this.Publish();
	}

	private void Publish()
	{
		levels.Publish(
			configuration.StreamMaster,
			configuration.StreamMuted,
			configuration.Levels,
			configuration.Muted,
			configuration.MuteInBackground);
	}
}
