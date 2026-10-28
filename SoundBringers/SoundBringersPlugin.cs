using System;

using Dalamud.Game.Command;
using Dalamud.Plugin;

namespace SoundBringers;

public class SoundBringersPlugin: IDalamudPlugin
{
	private const string CommandName = "/soundbringers";

	private readonly SoundBringersConfiguration configuration;

	public SoundBringersPlugin(IDalamudPluginInterface pluginInterface)
	{
		pluginInterface.Create<Service>();

		this.configuration = Service.PluginInterface.GetPluginConfig() as SoundBringersConfiguration ??
		                     new SoundBringersConfiguration();

		Service.CommandManager.AddHandler(
			CommandName,
			new CommandInfo(this.OnCommand) { HelpMessage = "Open the SoundBringers settings." });
	}

	private void OnCommand(string command, string arguments)
	{
		// Window lands later
		Service.PluginLog.Information("/soundbringers invoked");
	}

	protected virtual void Dispose(bool disposing)
	{
		if (!disposing)
		{
			return;
		}

		Service.CommandManager.RemoveHandler(CommandName);
	}

	public void Dispose()
	{
		this.Dispose(true);
		GC.SuppressFinalize(this);
	}
}
