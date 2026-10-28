using System;
using System.Buffers.Binary;

using SoundBringers.Dsp;
using SoundBringers.Game;

namespace SoundBringers.Mix;

// Audio thread: copies what the worker needs of one voice into the period's slot, around the game's render of it. Before: the voice,
// its sound objects, the bus block and the queued audio the period can reach; after: the buffers the game queued meanwhile and the
// voice's filter. Reads only, allocates nothing.
internal sealed class PeriodCopier(MemoryRead read)
{
	public const int MaxNeedFrames = 4096;
	public const int PostBytes = 0x50; // voice +0xD8 .. +0x128: queued count, head, read offset, both queue entries
	public const int EffectBack = 8; // the game's effect list holds the ICoreEffect pointer = object + 8
	private const int PostEntries = GameAudio.VoiceBuffers - GameAudio.VoiceQueuedBufferCount;

	// the voice being copied, between BeginVoice and EndVoice
	private VoiceRecord pending;
	private int index;
	private int mark;
	private int need;
	private int bytesPerFrame;

	// before the game's render; true when EndVoice must follow
	public bool BeginVoice(PeriodSlot slot, nint voice)
	{
		this.mark = slot.Used;
		this.index = slot.VoiceCount;
		this.pending = default;
		if (this.index >= slot.VoiceCapacity ||
		    !slot.TryReserve(PeriodSlot.VoiceBytes, out int voiceAt) ||
		    !slot.TryReserve(SoundTerms.SeadBytes, out int seadAt) ||
		    !slot.TryReserve(SoundTerms.ChildBytes, out int childAt) ||
		    !slot.TryReserve(SoundTerms.RootBytes, out int rootAt))
		{
			return this.Skip(slot);
		}

		Span<byte> voiceObject = slot.At(voiceAt, PeriodSlot.VoiceBytes);
		if (!read(voice, voiceObject))
		{
			return this.Skip(slot);
		}

		int state = BinaryPrimitives.ReadInt32LittleEndian(voiceObject[GameAudio.VoiceState..]);
		int queued = BinaryPrimitives.ReadInt32LittleEndian(voiceObject[GameAudio.VoiceQueuedBufferCount..]);
		int inChannels = BinaryPrimitives.ReadInt32LittleEndian(voiceObject[GameAudio.VoiceInputChannels..]);
		int sampleType = BinaryPrimitives.ReadInt32LittleEndian(voiceObject[GameAudio.VoiceSampleType..]);
		if (state != GameAudio.VoiceStateRendered || queued <= 0 || inChannels is < 1 or > 16 || sampleType is not (1 or 2))
		{
			return this.Skip(slot);
		}

		// the sound's objects, read once
		Span<byte> root = slot.At(rootAt, SoundTerms.RootBytes);
		nint callback = (nint)BinaryPrimitives.ReadInt64LittleEndian(voiceObject[GameAudio.VoiceCallback..]);
		if (!VoiceVolume.ReadTerms(read, callback, slot.At(seadAt, SoundTerms.SeadBytes), slot.At(childAt, SoundTerms.ChildBytes), root, out nint sound))
		{
			return this.Skip(slot);
		}

		this.pending.Voice = voice;
		this.pending.Sound = sound;
		this.pending.VoiceOffset = voiceAt;
		this.pending.SeadOffset = seadAt;
		this.pending.ChildOffset = childAt;
		this.pending.RootOffset = rootAt;
		Array.Clear(slot.BufferLengths, this.index * 4, 4);
		this.Bus(slot, root[GameAudio.RootBusId]);

		int voiceRate = BinaryPrimitives.ReadInt32LittleEndian(voiceObject[GameAudio.VoiceSampleRate..]);
		float pitch = BinaryPrimitives.ReadSingleLittleEndian(voiceObject[(GameAudio.VoiceCurrentParams + GameAudio.ParamsPitch)..]);
		float step = (voiceRate == slot.Rate ? 1f : voiceRate / (float)slot.Rate) * pitch;
		if (!(step > 0f) || !(step < 64f))
		{
			this.Commit(slot);
			return false;
		}

		this.pending.StepOk = true;
		this.bytesPerFrame = inChannels * (sampleType == 1 ? 2 : 4);
		int frames = (int)MathF.Ceiling(slot.Frames * step) + 4;
		this.need = Math.Min(frames, MaxNeedFrames);
		this.pending.Count = Math.Min(queued, GameAudio.VoiceBufferCount);
		this.pending.Head = (int)((uint)BinaryPrimitives.ReadInt32LittleEndian(voiceObject[GameAudio.VoiceCurrentBufferIndex..]) % GameAudio.VoiceBufferCount);
		this.pending.Read = Math.Max(BinaryPrimitives.ReadInt32LittleEndian(voiceObject[GameAudio.VoiceBufferReadOffset..]), 0);
		for (int i = 0; i < this.pending.Count; i++)
		{
			int queueSlot = (this.pending.Head + i) % GameAudio.VoiceBufferCount;

			// from the frame the game reads next (the renderer indexes whole frames), as far as this period can reach
			int skip = i == 0 ? this.pending.Read / this.bytesPerFrame * this.bytesPerFrame : 0;
			if (this.CopyBuffer(slot, voiceObject[(GameAudio.VoiceBuffers + (queueSlot * GameAudio.QueuedBufferSize))..], skip, queueSlot) < 0)
			{
				return this.Skip(slot);
			}
		}

		return true;
	}

	// after the game's render: the buffers it queued meanwhile, then the filter
	public void EndVoice(PeriodSlot slot, nint voice)
	{
		ReadOnlySpan<byte> voiceObject = slot.At(this.pending.VoiceOffset, PeriodSlot.VoiceBytes);

		// what the game's buffer-end callback queued: the queue entries behind the ones still left of the copy's. A pop moves the head
		// (two pops in one period leave it, caught by the read offset going back).
		int extras = 0;
		Span<byte> after = stackalloc byte[PostBytes];
		if (read(voice + GameAudio.VoiceQueuedBufferCount, after))
		{
			int count = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(after), 0, GameAudio.VoiceBufferCount);
			int head = (int)((uint)BinaryPrimitives.ReadInt32LittleEndian(after[4..]) % GameAudio.VoiceBufferCount);
			int readOffset = BinaryPrimitives.ReadInt32LittleEndian(after[8..]);
			RefillInference.Result refill = RefillInference.Infer(this.pending.Head, this.pending.Count, this.pending.Read, count, head, readOffset);
			for (int i = refill.FirstNew; i < count && extras < 2; i++)
			{
				int queueSlot = (head + i) % GameAudio.VoiceBufferCount;
				int length = this.CopyBuffer(slot, after[(PostEntries + (queueSlot * GameAudio.QueuedBufferSize))..], 0, 2 + extras);
				if (length < 0)
				{
					this.Abandon(slot);
					return;
				}

				if (length > 0)
				{
					extras++;
				}
			}
		}

		this.pending.Extras = extras;
		if (BinaryPrimitives.ReadInt32LittleEndian(voiceObject[GameAudio.VoiceEffectCount..]) > 0)
		{
			nint filter = (nint)BinaryPrimitives.ReadInt64LittleEndian(voiceObject[GameAudio.VoiceEffects..]);
			if (filter != 0)
			{
				if (!slot.TryReserve(VoiceFilter.Size, out int at))
				{
					this.Abandon(slot);
					return;
				}

				this.pending.HasFilter = true;
				this.pending.FilterOffset = at;
				this.pending.FilterLength = Math.Max(VoiceFilter.Copy(read, filter - EffectBack, slot.At(at, VoiceFilter.Size)), 0);
			}
		}

		this.Commit(slot);
	}

	// a fault: releases what the voice copied
	public void Abandon(PeriodSlot slot) => slot.Used = this.mark;

	// the bus's block, once per period (a failed read is tried again by the next voice of the bus)
	private void Bus(PeriodSlot slot, int id)
	{
		if (id < 1 || id > slot.BusCount || slot.BusPointers[id - 1] == 0 || slot.BusOffsets[id - 1] >= 0)
		{
			return;
		}

		int before = slot.Used;
		if (slot.TryReserve(VolumeCache.BusBytes, out int at) && read(slot.BusPointers[id - 1], slot.At(at, VolumeCache.BusBytes)))
		{
			slot.BusOffsets[id - 1] = at;

			// the block stays when the voice is dropped
			this.mark = slot.Used;
		}
		else
		{
			slot.Used = before;
		}
	}

	// copies up to the needed bytes of the queue entry's buffer, starting at skip, into the arena as the voice's buffer k; returns the
	// length (0 when unreadable, -1 when the slot is full)
	private int CopyBuffer(PeriodSlot slot, ReadOnlySpan<byte> entry, int skip, int k)
	{
		nint data = (nint)BinaryPrimitives.ReadInt64LittleEndian(entry[GameAudio.QueuedBufferData..]);
		long bytes = BinaryPrimitives.ReadInt64LittleEndian(entry[GameAudio.QueuedBufferBytes..]) - skip;
		if (data == 0 || bytes <= 0)
		{
			return 0;
		}

		int length = (int)Math.Min(bytes, (long)this.need * this.bytesPerFrame);
		int before = slot.Used;
		if (!slot.TryReserve(length, out int at))
		{
			return -1;
		}

		if (!read(data + skip, slot.At(at, length)))
		{
			slot.Used = before;
			return 0;
		}

		slot.BufferOffsets[(this.index * 4) + k] = at;
		slot.BufferLengths[(this.index * 4) + k] = length;
		return length;
	}

	// a voice that is not copied (failed checks, or no room)
	private bool Skip(PeriodSlot slot)
	{
		slot.Used = this.mark;
		return false;
	}

	private void Commit(PeriodSlot slot)
	{
		slot.Voices[this.index] = this.pending;
		slot.VoiceCount++;
	}
}
