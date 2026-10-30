using System;
using System.Collections.Generic;
using System.Linq;

using SoundBringers.Dsp;

namespace SoundBringers.Output;

// The split stream: one StreamOutput per category sink, each playing its own ring
public sealed class SplitOutput : IDisposable
{
	private readonly StreamOutput[] outputs;
	private string listed = string.Empty;

	public SplitOutput(SampleRing[] rings)
	{
		this.Rings = rings;
		this.outputs = [.. rings.Select(r => new StreamOutput(r))];
	}

	public SampleRing[] Rings { get; }

	// every sink is open
	public bool HasDevice => this.outputs.All(o => o.HasDevice);

	public bool DeviceLost => this.outputs.Any(o => o.DeviceLost);

	public bool OpenFailed => this.outputs.Any(o => o.OpenFailed);

	// opens the sinks that are not open yet and are listed; false when one is missing from the device list
	public bool Start(OutputMode mode)
	{
		if (this.HasDevice)
			return true;

		IReadOnlyList<OutputDevice> devices = this.outputs[0].Devices();
		string names = string.Join(", ", devices.Select(d => $"'{d.Name}'"));
		if (names != this.listed)
		{
			this.listed = names;
			Service.PluginLog.Information($"Output devices: {names}");
		}

		bool all = true;
		for (int i = 0; i < this.outputs.Length; i++)
		{
			if (this.outputs[i].HasDevice)
				continue;

			OutputDevice? device = DevicePicker.ByName(devices, LinuxSink.SplitNames[i]);
			if (device == null)
				all = false;
			else
				this.outputs[i].Start(device.Value, mode);
		}

		return all;
	}

	public void Poll()
	{
		foreach (StreamOutput output in this.outputs)
			output.Poll();
	}

	// tear down what is lost or half open, so the next Start retries it
	public void ReleaseLost()
	{
		foreach (StreamOutput output in this.outputs)
		{
			if (output.DeviceLost)
				output.Release();
		}
	}

	public void Stop()
	{
		foreach (StreamOutput output in this.outputs)
			output.Stop();
	}

	public void Dispose() => this.Stop();
}
