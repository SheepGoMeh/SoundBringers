using System;
using System.Buffers.Binary;
using System.Runtime.Intrinsics;

namespace SoundBringers.Dsp;

// fields the game's voice render (sub_141EB84F0) reads; offsets in GameAudio
public ref struct VoiceInput
{
	public ReadOnlySpan<byte> Buf0;
	public ReadOnlySpan<byte> Buf1;
	public ReadOnlySpan<byte> Extra0; // buffers queued after the ones above (the game's buffer-end callback refills mid-period)
	public ReadOnlySpan<byte> Extra1;
	public int ExtraCount;
	public int QueuedCount;
	public int CurrentIndex;
	public int ReadOffset;
	public double Position;
	public double Delay; // +0xC8: negative = output frames still to wait before the first sample
	public float Frac;
	public int SampleType; // 1 = s16, 2 = float
	public int SampleRate;
	public float Pitch;
	public int InChannels;
	public int OutChannels;
	public ReadOnlySpan<float> Matrix; // rows = output channels, row * InChannels + input
	public ReadOnlySpan<byte> History; // +0x9C0: previous frame, 16 x (s16 or float)

	// every field but the buffers, from a copy of the voice object (0xA48 bytes)
	public static VoiceInput FromObject(ReadOnlySpan<byte> span, int outChannels)
	{
		return new VoiceInput
		{
			QueuedCount = BinaryPrimitives.ReadInt32LittleEndian(span[0xD8..]),
			CurrentIndex = BinaryPrimitives.ReadInt32LittleEndian(span[0xDC..]),
			ReadOffset = BinaryPrimitives.ReadInt32LittleEndian(span[0xE0..]),
			Position = BinaryPrimitives.ReadDoubleLittleEndian(span[0xC0..]),
			Delay = BinaryPrimitives.ReadDoubleLittleEndian(span[0xC8..]),
			Frac = BinaryPrimitives.ReadSingleLittleEndian(span[0x9BC..]),
			SampleType = BinaryPrimitives.ReadInt32LittleEndian(span[0xB8..]),
			SampleRate = BinaryPrimitives.ReadInt32LittleEndian(span[0xBC..]),
			Pitch = BinaryPrimitives.ReadSingleLittleEndian(span[0x534..]),
			InChannels = BinaryPrimitives.ReadInt32LittleEndian(span[0xD4..]),
			OutChannels = outChannels, // game maps the format at +0xA40 (sub_141E9DF00) to the mixer width
			Matrix = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(span[0x538..0x938]),
			History = span[0x9C0..0xA00],
		};
	}
}

public static class VoiceRenderer
{
	private const float S16Scale = 0.00003051851f;
	private const float Audible = 0.0001f;
	private const float Denormal = 1e-15f;

	// the audible branch only (volume or previous above 0.0001); the game's state is not advanced.
	// returns the output frames the queued buffers could feed; the rest stay silent (the game's buffer-end callback may queue more)
	public static int Render(in VoiceInput v, float volume, float previousVolume, int frames, int mixerRate, Span<float> output)
	{
		int outCh = v.OutChannels;
		int inCh = Math.Clamp(v.InChannels, 1, 16);
		bool s16 = v.SampleType == 1;
		output[..(frames * outCh)].Clear();
		float step = (v.SampleRate == mixerRate ? 1f : v.SampleRate / (float)mixerRate) * v.Pitch;
		if (!(step > 0f) || (!s16 && v.SampleType != 2) || (!(volume > Audible) && !(previousVolume > Audible)))
		{
			return frames;
		}

		// start delay: silent frames until the position reaches zero
		double delay = v.Delay;
		if ((float)(-frames * step) >= delay)
		{
			return frames;
		}

		int remaining = frames;
		int outPos = 0;
		while (delay < 0 && remaining > 0)
		{
			remaining--;
			delay = step + delay;
			outPos += outCh;
		}

		if (remaining <= 0)
		{
			return frames;
		}

		// the s16 scale is folded into the per-input matrix vectors
		float scale = s16 ? S16Scale : 1f;
		Span<float> gains = stackalloc float[inCh * outCh];
		for (int c = 0; c < inCh; c++)
		{
			for (int r = 0; r < outCh; r++)
			{
				gains[(c * outCh) + r] = scale * v.Matrix[(r * inCh) + c];
			}
		}

		Span<float> hist = stackalloc float[inCh];
		for (int c = 0; c < inCh; c++)
		{
			hist[c] = s16
				? BinaryPrimitives.ReadInt16LittleEndian(v.History[(c * 2)..])
				: BinaryPrimitives.ReadSingleLittleEndian(v.History[(c * 4)..]);
		}

		Span<float> mix = stackalloc float[inCh];
		float ramp = (volume - previousVolume) / remaining;
		float current = previousVolume;
		float frac = v.Frac;
		int bytesPerFrame = inCh * (s16 ? 2 : 4);
		int first = Math.Clamp(v.QueuedCount, 0, 2);
		int queued = first + v.ExtraCount;
		int seq = 0;
		ReadOnlySpan<byte> buffer = Buffer(v, first, seq);
		int pos = v.ReadOffset / bytesPerFrame;
		int avail = (buffer.Length - v.ReadOffset) / bytesPerFrame;

		while (true)
		{
			while (avail <= 0)
			{
				if (queued > 0)
				{
					queued--;
					seq++;
				}

				if (queued <= 0)
				{
					return outPos / outCh;
				}

				buffer = Buffer(v, first, seq);
				pos = 0;
				avail = buffer.Length / bytesPerFrame;
			}

			// before the first consumed frame: blend the history with the buffer start
			if (frac < 1f)
			{
				while (remaining > 0)
				{
					for (int c = 0; c < inCh; c++)
					{
						mix[c] = ((Sample(buffer, s16, inCh, pos, c) - hist[c]) * frac) + hist[c];
					}

					Emit(output.Slice(outPos, outCh), mix, gains, current);
					outPos += outCh;
					remaining--;
					current = ((current + ramp) + Denormal) - Denormal;
					frac = step + frac;
					if (frac >= 1f)
					{
						break;
					}
				}
			}

			int consume = (int)frac;
			bool consumed = false;
			if (consume > 0)
			{
				avail -= consume;
				pos += consume;
				frac -= consume;
				consumed = true;
			}

			while (remaining > 0 && avail > 0)
			{
				for (int c = 0; c < inCh; c++)
				{
					float prev = Sample(buffer, s16, inCh, pos - 1, c);
					mix[c] = ((Sample(buffer, s16, inCh, pos, c) - prev) * frac) + prev;
				}

				Emit(output.Slice(outPos, outCh), mix, gains, current);
				outPos += outCh;
				remaining--;
				current = ((current + ramp) + Denormal) - Denormal;
				float next = step + frac;
				int n = (int)next;
				avail -= n;
				pos += n;
				frac = next - n;
			}

			// ran past the end of the buffer: step back and carry the overshoot in frac
			if (avail < 0)
			{
				pos += avail;
				frac -= avail;
			}

			if (consumed)
			{
				for (int c = 0; c < inCh; c++)
				{
					hist[c] = Sample(buffer, s16, inCh, pos - 1, c);
				}
			}

			if (remaining <= 0)
			{
				return frames;
			}
		}
	}

	// the seq-th queued buffer: the voice's own two in queue order, then the ones queued during the game's render
	private static ReadOnlySpan<byte> Buffer(in VoiceInput v, int first, int seq)
	{
		if (seq < first)
		{
			return ((v.CurrentIndex + seq) & 1) == 0 ? v.Buf0 : v.Buf1;
		}

		return seq == first ? v.Extra0 : v.Extra1;
	}

	private static float Sample(ReadOnlySpan<byte> buffer, bool s16, int inCh, int frame, int c) => s16
		? BinaryPrimitives.ReadInt16LittleEndian(buffer[(((frame * inCh) + c) * 2)..])
		: BinaryPrimitives.ReadSingleLittleEndian(buffer[(((frame * inCh) + c) * 4)..]);

	// weighted sum of the input channels (multiply, then add), then the ramped volume
	private static void Emit(Span<float> frame, ReadOnlySpan<float> mix, ReadOnlySpan<float> gains, float volume)
	{
		int outCh = frame.Length;

		// an 8-channel frame is one vector: the same multiply-add per channel, in the same order
		if (outCh == 8 && Vector256.IsHardwareAccelerated)
		{
			Vector256<float> sum = Vector256.Create((ReadOnlySpan<float>)frame);
			for (int c = 0; c < mix.Length; c++)
			{
				sum = (Vector256.Create(mix[c]) * Vector256.Create(gains.Slice(c * 8, 8))) + sum;
			}

			(sum * Vector256.Create(volume)).CopyTo(frame);
			return;
		}

		for (int c = 0; c < mix.Length; c++)
		{
			for (int r = 0; r < outCh; r++)
			{
				frame[r] = (mix[c] * gains[(c * outCh) + r]) + frame[r];
			}
		}

		for (int r = 0; r < outCh; r++)
		{
			frame[r] *= volume;
		}
	}
}
