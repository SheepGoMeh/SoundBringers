using System;

namespace SoundBringers.Dsp;

// Which queue entries the game's buffer-end callback added during its voice render, from the voice's queue before and after.
// The game pops a buffer (count - 1, head + 1 mod 2, read offset 0) when it runs out, then the callback may submit a new one at the
// tail. The render copied the queue before the call, so the entries after the call that are not left over from it are the new buffers.
//
// Limits (only the head's parity and the read offset are seen): with one buffer queued, two pops read as none, so the period's tail is
// silent (flagged Ambiguous when the read offset went back); three pops read as one, four as none; a refill queued and used up within
// the period leaves no trace. Hooking the callback's buffer-start (vf+8) would make it exact.
public static class RefillInference
{
	public readonly record struct Result(int Popped, int FirstNew, bool Ambiguous);

	// preCount / postCount: queued buffers; head: index of the current buffer (0 or 1); read: byte offset in the current buffer.
	// FirstNew: index into the queue after the call of the first new buffer (postCount when there is none)
	public static Result Infer(int preHead, int preCount, int preRead, int postCount, int postHead, int postRead)
	{
		int popped = (postHead - preHead) & 1;
		bool ambiguous = false;
		if (popped == 0 && postRead < preRead)
		{
			if (preCount == 2)
			{
				popped = 2;
			}
			else
			{
				ambiguous = true;
			}
		}

		return new Result(popped, Math.Clamp(preCount - popped, 0, Math.Max(postCount, 0)), ambiguous);
	}
}
