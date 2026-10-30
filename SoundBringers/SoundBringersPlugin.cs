using System;

using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.System.Framework;

using SoundBringers.Dsp;
using SoundBringers.Game;
using SoundBringers.Mix;
using SoundBringers.Output;
using SoundBringers.Windows;

namespace SoundBringers;

public class SoundBringersPlugin: IDalamudPlugin
{
	private const string CommandName = "/soundbringers";

	private readonly SoundBringersConfiguration configuration;
	private readonly SampleRing ring = new(9600, 2);
	private readonly StreamOutput output;
	private readonly StreamLevels levels = new();
	private readonly WindowSystem windowSystem = new("SoundBringers");
	private readonly MainWindow window;

	private ShadowMixer? mixer;
	private long lastPollMs;
	private StreamState state = StreamState.GameDataMissing;

	public SoundBringersPlugin(IDalamudPluginInterface pluginInterface)
	{
		pluginInterface.Create<Service>();

		this.configuration = Service.PluginInterface.GetPluginConfig() as SoundBringersConfiguration ??
		                     new SoundBringersConfiguration();

		this.configuration.Validate();
		bool gameDataOk = GameAudio.Initialize();

		this.output = new StreamOutput(this.ring);
		this.TryStart();
		this.levels.Publish(
			this.configuration.StreamMaster,
			this.configuration.StreamMuted,
			this.configuration.Levels,
			this.configuration.Muted,
			this.configuration.MuteInBackground);
		Service.Framework.Update += this.OnUpdate;

		this.window = new MainWindow(this.configuration, this.output, this.levels, () => this.state);
		this.windowSystem.AddWindow(this.window);
		Service.PluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
		Service.PluginInterface.UiBuilder.OpenMainUi += this.window.Toggle;
		Service.PluginInterface.UiBuilder.OpenConfigUi += this.window.Toggle;

		Service.CommandManager.AddHandler(
			CommandName,
			new CommandInfo((_, _) => this.window.Toggle()) { HelpMessage = "Open the SoundBringers settings." });

		// last: nothing after it can fail and leave its hooks installed
		if (gameDataOk)
		{
			try
			{
				this.mixer = new ShadowMixer(this.levels, this.ring);
			}
			catch (Exception e)
			{
				Service.PluginLog.Error(e, "Shadow mixer not installed");
			}
		}
	}

	private void TryStart()
	{
		OutputDevice? device = DevicePicker.Pick(this.output.Devices(), this.configuration.DeviceId);
		if (device != null)
		{
			this.output.Start(device.Value, this.configuration.OutputMode);
		}
		else
		{
			// no device at all is "No virtual cable", not a failed open
			this.output.Stop();
		}
	}

	private void OnUpdate(IFramework framework)
	{
		try
		{
			this.levels.Background = WindowInactive();
			long now = Environment.TickCount64;
			if (now - this.lastPollMs >= 1000)
			{
				this.lastPollMs = now;
				this.output.Poll();
				if (!this.output.HasDevice || this.output.DeviceLost)
				{
					this.output.Release();
					this.TryStart();
				}

				this.mixer?.Refresh();
				this.state = this.Evaluate();
				if (this.mixer != null)
				{
					this.mixer.Enabled = this.state is not (StreamState.SpatialDisabled or StreamState.GameDataMissing or StreamState.Unsupported);
					this.mixer.Inline = this.configuration.InlineMix;
				}
			}
		}
		catch (Exception e)
		{
			Service.PluginLog.Error(e, "Update failed");
		}
	}

	private StreamState Evaluate()
	{
		bool ready = GameAudio.Ready && this.mixer != null;
		bool spatial = ready && GameAudio.SpatialActive();
		return StreamStatus.Of(
			ready,
			spatial,
			ready ? GameAudio.EndpointChannels() : 2,
			this.output.HasDevice,
			this.output.DeviceLost,
			!ready || this.mixer!.HasGraph,
			this.output.OpenFailed);
	}

	private static unsafe bool WindowInactive() => Framework.Instance()->WindowInactive;

	protected virtual void Dispose(bool disposing)
	{
		if (!disposing)
		{
			return;
		}

		Service.CommandManager.RemoveHandler(CommandName);
		Service.PluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
		Service.PluginInterface.UiBuilder.OpenMainUi -= this.window.Toggle;
		Service.PluginInterface.UiBuilder.OpenConfigUi -= this.window.Toggle;
		this.windowSystem.RemoveAllWindows();
		Service.Framework.Update -= this.OnUpdate;
		this.mixer?.Dispose();
		this.output.Dispose();
	}

	public void Dispose()
	{
		this.Dispose(true);
		GC.SuppressFinalize(this);
	}
}
