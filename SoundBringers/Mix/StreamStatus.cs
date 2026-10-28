namespace SoundBringers.Mix;

public enum StreamState
{
	Ok,
	Approximate,
	SpatialDisabled,
	NoDevice,
	DeviceLost,
	GameDataMissing,
	Unsupported,
	OpenFailed,
}

// what the window shows and whether the mixer may stream
public static class StreamStatus
{
	public static StreamState Of(bool gameDataOk, bool spatialActive, int endpointChannels, bool hasDevice, bool deviceLost, bool formatSupported = true, bool openFailed = false)
	{
		if (!gameDataOk)
			return StreamState.GameDataMissing;
		if (spatialActive)
			return StreamState.SpatialDisabled;
		if (!formatSupported)
			return StreamState.Unsupported;
		if (deviceLost)
			return StreamState.DeviceLost;
		if (!hasDevice && openFailed)
			return StreamState.OpenFailed;
		if (!hasDevice)
			return StreamState.NoDevice;
		return endpointChannels == 2 ? StreamState.Ok : StreamState.Approximate;
	}

	public static string Text(StreamState s) => s switch
	{
		StreamState.Ok => "Streaming",
		StreamState.Approximate => "Streaming (surround speakers: the mix is folded to stereo)",
		StreamState.SpatialDisabled => "Off: turn off spatial audio in the game's sound settings",
		StreamState.NoDevice => "No virtual audio cable found, install one such as VB-Cable",
		StreamState.DeviceLost => "Output device lost, retrying",
		StreamState.OpenFailed => "Can't open the output device, retrying (see /xllog)",
		StreamState.Unsupported => "Off: the game's audio format isn't supported (see /xllog)",
		_ => "Off: this game version isn't supported",
	};
}
