using System;
using System.Collections.Generic;

namespace SoundBringers.Output;

public readonly record struct OutputDevice(string Id, string Name);

public static class DevicePicker
{
	public static OutputDevice? Pick(IReadOnlyList<OutputDevice> devices, string? savedId)
	{
		foreach (OutputDevice device in devices)
		{
			if (savedId != null && device.Id == savedId)
			{
				return device;
			}
		}

		foreach (OutputDevice device in devices)
		{
			if (device.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
			{
				return device;
			}
		}

		foreach (OutputDevice device in devices)
		{
			if (LinuxSink.Matches(device.Name, LinuxSink.Name))
			{
				return device;
			}
		}

		return null;
	}

	public static OutputDevice? ByName(IReadOnlyList<OutputDevice> devices, string sink)
	{
		foreach (OutputDevice device in devices)
		{
			if (LinuxSink.Matches(device.Name, sink))
			{
				return device;
			}
		}

		return null;
	}
}
