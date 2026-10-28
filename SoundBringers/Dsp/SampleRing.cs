using System;
using System.Threading;

namespace SoundBringers.Dsp;

public sealed class SampleRing(int capacityFrames, int channels)
{
	private readonly float[] buffer = new float[capacityFrames * channels];
	private long writeIndex;
	private long claimedIndex;
	private long readIndex;

	public int FillFrames
	{
		get
		{
			long w = Volatile.Read(ref this.writeIndex);
			long r = Volatile.Read(ref this.readIndex);
			return (int)Math.Min(w - r, capacityFrames);
		}
	}

	// producer only advances writeIndex; consumer detects overrun and skips ahead
	public void Write(ReadOnlySpan<float> interleaved)
	{
		int frames = interleaved.Length / channels;
		long w = this.writeIndex;
		if (frames > capacityFrames)
		{
			interleaved = interleaved[((frames - capacityFrames) * channels)..];
			w += frames - capacityFrames;
			frames = capacityFrames;
		}

		// claim before copying so the consumer can see an in-flight overwrite
		Volatile.Write(ref this.claimedIndex, w + frames);
		Interlocked.MemoryBarrier();
		int pos = (int)(w % capacityFrames);
		int first = Math.Min(frames, capacityFrames - pos);
		interleaved[..(first * channels)].CopyTo(this.buffer.AsSpan(pos * channels));
		interleaved[(first * channels)..].CopyTo(this.buffer);
		Volatile.Write(ref this.writeIndex, w + frames);
	}

	public int Read(Span<float> interleaved)
	{
		int want = interleaved.Length / channels;
		while (true)
		{
			long w = Volatile.Read(ref this.writeIndex);
			long r = Math.Max(this.readIndex, w - capacityFrames);
			int n = (int)Math.Min(want, w - r);
			int pos = (int)(r % capacityFrames);
			int first = Math.Min(n, capacityFrames - pos);
			this.buffer.AsSpan(pos * channels, first * channels).CopyTo(interleaved);
			this.buffer.AsSpan(0, (n - first) * channels).CopyTo(interleaved[(first * channels)..]);

			// producer may be overwriting or have lapped the copied region
			Interlocked.MemoryBarrier();
			if (Volatile.Read(ref this.claimedIndex) - r > capacityFrames)
			{
				continue;
			}

			interleaved[(n * channels)..].Clear();
			Volatile.Write(ref this.readIndex, r + n);
			return n;
		}
	}

	// drops the oldest frames beyond maxFrames
	public void Trim(int maxFrames)
	{
		long target = Volatile.Read(ref this.writeIndex) - maxFrames;
		if (target > this.readIndex)
		{
			Volatile.Write(ref this.readIndex, target);
		}
	}
}
