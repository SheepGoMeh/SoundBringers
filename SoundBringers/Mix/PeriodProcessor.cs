using System;
using System.Buffers.Binary;

using SoundBringers.Dsp;
using SoundBringers.Game;

namespace SoundBringers.Mix;

// Worker: turns one copied period into the stream's audio. Owns everything with a history, so it runs on one thread only.
internal sealed class PeriodProcessor
{
	private const float DefaultActiveVolume = 0.6675f;
	private const float Audible = 0.0001f;
	private const int MaxSends = 16;

	private readonly VolumeCache volume = new();
	private readonly VoiceStateTable table = new();

	public enum Outcome
	{
		Silence, // no graph: the stream is silent this period
		Output, // graph.Output holds the period
		NonFinite, // the output was not finite: silence, and the effects need rebuilding
	}

	// called with a voice's exception; the voice is dropped and the period goes on
	public Action<Exception>? Fault { get; set; }

	// a dropped period: voices from before it start over instead of ramping from stale volumes
	public void Gap()
	{
		this.table.BeginPeriod();
		this.volume.Gap();
	}

	public Outcome Process(PeriodSlot slot)
	{
		ShadowGraph? graph = slot.Graph;
		if (graph == null)
		{
			return Outcome.Silence;
		}

		graph.LoadEffects(slot);
		this.volume.Begin(slot);
		this.table.BeginPeriod();
		float active = this.volume.ActiveVolume;
		float gain = slot.Levels!.Master * (active > 0f ? active : DefaultActiveVolume);
		for (int i = 0; i < slot.VoiceCount; i++)
		{
			try
			{
				this.Voice(slot, graph, i);
			}
			catch (Exception e)
			{
				this.Fault?.Invoke(e);
			}
		}

		graph.Process(gain, graph.Output);

		// an effect that went non-finite would stay that way
		return Finite(graph.Output) ? Outcome.Output : Outcome.NonFinite;
	}

	private void Voice(PeriodSlot slot, ShadowGraph graph, int index)
	{
		VoiceRecord r = slot.Voices[index];
		ReadOnlySpan<byte> voiceObject = slot.At(r.VoiceOffset, PeriodSlot.VoiceBytes);
		ReadOnlySpan<byte> sead = slot.At(r.SeadOffset, SoundTerms.SeadBytes);
		ReadOnlySpan<byte> child = slot.At(r.ChildOffset, SoundTerms.ChildBytes);
		ReadOnlySpan<byte> root = slot.At(r.RootOffset, SoundTerms.RootBytes);
		int busId = root[GameAudio.RootBusId];
		float level = slot.Levels!.Level(Categories.Of(busId));
		VoiceStateTable.History last = this.table.Last(r.Voice, r.Sound);

		// a level of 0 only fades a voice that was rendered a moment ago out; any other voice at level 0 costs nothing
		if (!(level > 0f) && !(last.Known && last.LastVolume > Audible))
		{
			// keeps the history at 0, so an unmute ramps up instead of stepping
			this.table.Store(r.Voice, r.Sound, 0f, false, 1f);
			return;
		}

		// the committed volumes lag the sound chain while anything fades; scaling them by stream / game keeps the lag (see VolumeFollow)
		float composed = VoiceVolume.StreamVolume(this.volume, sead, child, root, level);
		float game = VoiceVolume.StreamVolume(this.volume, sead, child, root, 1f, true);
		VolumeFollow.Follow follow = VolumeFollow.Choose(
			composed,
			game,
			BinaryPrimitives.ReadSingleLittleEndian(voiceObject[(GameAudio.VoiceCurrentParams + GameAudio.ParamsVolume)..]),
			BinaryPrimitives.ReadSingleLittleEndian(voiceObject[GameAudio.VoicePreviousVolume..]),
			this.volume.Settled(busId),
			last);
		float voiceVolume = follow.Volume;
		float previous = follow.Previous;
		this.table.Store(r.Voice, r.Sound, voiceVolume, follow.RatioPath, follow.Ratio);
		if ((!(voiceVolume > Audible) && !(previous > Audible)) || !r.StepOk)
		{
			return;
		}

		VoiceInput input = VoiceInput.FromObject(voiceObject, graph.Channels);
		input.Buf0 = slot.At(slot.BufferOffsets[index * 4], slot.BufferLengths[index * 4]);
		input.Buf1 = slot.At(slot.BufferOffsets[(index * 4) + 1], slot.BufferLengths[(index * 4) + 1]);
		input.Extra0 = slot.At(slot.BufferOffsets[(index * 4) + 2], r.Extras > 0 ? slot.BufferLengths[(index * 4) + 2] : 0);
		input.Extra1 = slot.At(slot.BufferOffsets[(index * 4) + 3], r.Extras > 1 ? slot.BufferLengths[(index * 4) + 3] : 0);
		input.ExtraCount = r.Extras;
		input.QueuedCount = r.Count;
		input.CurrentIndex = r.Head;
		input.ReadOffset = 0;
		Span<float> output = graph.Scratch;
		VoiceRenderer.Render(in input, voiceVolume, previous, graph.Frames, slot.Rate, output);

		// a render that is not finite would poison the filter's memory
		int length = graph.Frames * graph.Channels;
		if (!Finite(output[..length]))
		{
			return;
		}

		if (r.HasFilter)
		{
			VoiceFilter state = this.table.Filter(r.Voice, r.Sound);
			if (r.FilterLength > 0)
			{
				state.LoadCopy(slot.At(r.FilterOffset, r.FilterLength));
			}

			state.Process(output, graph.Frames, graph.Channels);

			// a finite but huge gain can still overflow in the filter: drop the voice and its memory, before any accumulator sees it
			if (!Finite(output[..length]))
			{
				state.Reset();
				return;
			}
		}

		int sends = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(voiceObject[GameAudio.VoiceSendCount..]), 0, MaxSends);
		for (int i = 0; i < sends; i++)
		{
			ReadOnlySpan<byte> send = voiceObject[(GameAudio.VoiceSends + (i * GameAudio.SendSize))..];
			int node = graph.IndexOf((nint)BinaryPrimitives.ReadInt64LittleEndian(send[GameAudio.SendDestination..]));
			if (node >= 0)
			{
				graph.Add(node, output, BinaryPrimitives.ReadSingleLittleEndian(send[GameAudio.SendGain..]));
			}
		}
	}

	private static bool Finite(ReadOnlySpan<float> samples)
	{
		foreach (float sample in samples)
		{
			if (!float.IsFinite(sample))
			{
				return false;
			}
		}

		return true;
	}
}
