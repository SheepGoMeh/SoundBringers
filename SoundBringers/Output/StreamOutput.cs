using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using NAudio.CoreAudioApi;
using NAudio.Wave;

using SoundBringers.Dsp;

namespace SoundBringers.Output;

public enum OutputMode
{
	Shared,
	LowLatency,
	Exclusive,
}

public sealed class StreamOutput(SampleRing ring) : IDisposable
{
	// Wine has no exclusive or IAudioClient3 low-latency mode worth offering
	public static bool IsWine { get; } = NativeLibrary.TryLoad("ntdll.dll", out nint ntdll) && NativeLibrary.TryGetExport(ntdll, "wine_get_version", out _);

	private const int Latency = 10;
	private const int TrimFrames = 2048;

	private WasapiPlayer? output;
	private MMDevice? device;
	private volatile bool lost;
	private string lastStartError = string.Empty;

	public bool HasDevice => this.output != null;

	// sticky: cleared by a successful Start or an explicit Stop
	public bool DeviceLost => this.lost;

	// the last attempt to open a device failed (cleared by a successful Start or an explicit Stop)
	public bool OpenFailed { get; private set; }

	public string? DeviceName { get; private set; }

	// exclusive was asked for but the device refused it, so the output runs shared
	public bool ExclusiveRefused { get; private set; }

	public IReadOnlyList<OutputDevice> Devices()
	{
		List<OutputDevice> list = [];
		try
		{
			using MMDeviceEnumerator enumerator = new();
			foreach (MMDevice d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
			{
				using (d)
				{
					list.Add(new OutputDevice(d.ID, d.FriendlyName));
				}
			}
		}
		catch (Exception e)
		{
			Service.PluginLog.Error(e, "Enumerating output devices failed");
		}

		return list;
	}

	public void Start(OutputDevice target, OutputMode mode)
	{
		this.Release();
		try
		{
			using MMDeviceEnumerator enumerator = new();
			this.device = enumerator.GetDevice(target.Id);
			this.ExclusiveRefused = false;
			if (mode == OutputMode.Exclusive)
			{
				try
				{
					this.Open(mode);
				}
				catch (Exception e)
				{
					this.ExclusiveRefused = true;
					Service.PluginLog.Warning($"Exclusive mode refused by {target.Name}, using shared: {e.Message}");
					this.CloseOutput();
					this.Open(OutputMode.Shared);
				}
			}
			else
			{
				this.Open(mode);
			}

			this.DeviceName = target.Name;
			this.lost = false;
			this.OpenFailed = false;
			this.lastStartError = string.Empty;
		}
		catch (Exception e)
		{
			// the plugin retries every second: say it once per distinct failure
			this.OpenFailed = true;
			if (e.Message != this.lastStartError)
			{
				this.lastStartError = e.Message;
				Service.PluginLog.Error(e, "Starting output failed");
			}

			this.Release();
		}
	}

	private void Open(OutputMode mode)
	{
		WasapiPlayerBuilder builder = new WasapiPlayerBuilder().WithDevice(this.device!).WithEventSync().WithLatency(Latency);
		builder = mode switch
		{
			OutputMode.Exclusive => builder.WithExclusiveMode(),
			// falls back to standard shared mode on its own when the engine can't honour it
			OutputMode.LowLatency => builder.WithSharedMode().WithLowLatency(false),
			_ => builder.WithSharedMode(),
		};
		this.output = builder.Build();
		this.output.PlaybackStopped += this.OnStopped;
		this.output.Init(new RingProvider(ring).ToWaveProvider());
		this.output.Play();
	}

	private void CloseOutput()
	{
		WasapiPlayer? o = this.output;
		this.output = null;
		if (o == null)
			return;

		try
		{
			o.PlaybackStopped -= this.OnStopped;
			o.Dispose();
		}
		catch (Exception)
		{
			// half-opened: nothing more to release
		}
	}

	public void Stop()
	{
		this.Release();
		this.lost = false;
		this.OpenFailed = false;
		this.lastStartError = string.Empty;
	}

	// check the active device state; call about once per second
	public void Poll()
	{
		if (this.StateLost())
		{
			this.lost = true;
		}
	}

	// tear down but keep the lost flag
	public void Release()
	{
		WasapiPlayer? o = this.output;
		MMDevice? d = this.device;
		this.output = null;
		this.device = null;
		this.DeviceName = null;
		try
		{
			if (o != null)
			{
				o.PlaybackStopped -= this.OnStopped;
				o.Stop();
				o.Dispose();
			}
		}
		catch (Exception e)
		{
			Service.PluginLog.Error(e, "Stopping output failed");
		}

		try
		{
			d?.Dispose();
		}
		catch (Exception e)
		{
			Service.PluginLog.Error(e, "Releasing device failed");
		}
	}

	public void Dispose() => this.Stop();

	// stop without exception comes from our own Stop, which unsubscribes first
	private void OnStopped(object? sender, StoppedEventArgs args)
	{
		if (sender != this.output)
		{
			return;
		}

		if (args.Exception != null)
		{
			Service.PluginLog.Warning(args.Exception, "Output stopped");
		}

		this.lost = true;
	}

	private bool StateLost()
	{
		try
		{
			return this.device != null && this.device.State != DeviceState.Active;
		}
		catch
		{
			return true;
		}
	}

	private sealed class RingProvider(SampleRing ring) : ISampleProvider
	{
		public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

		public int Read(Span<float> buffer)
		{
			ring.Trim(TrimFrames);
			ring.Read(buffer);
			return buffer.Length;
		}
	}
}
