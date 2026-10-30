using System;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SoundBringers.Dsp;

public static class MasterPath
{
	// 8 -> 2 fold of the game's final conversion (sub_141EA2EF0, gain 1.0): rows are the mixer's FL, FR, FC, LFE, BL, BR, SL, SR
	// spans over static data, not static arrays: no type initializer to run on the worker
	private static ReadOnlySpan<float> Left => [0.89f, 0.11f, 0.58f, 0.385f, 0.92f, 0f, 0f, 0f];

	private static ReadOnlySpan<float> Right => [0.11f, 0.89f, 0.58f, 0.385f, 0f, 0.92f, 0f, 0f];

	public static void FoldTo2(ReadOnlySpan<float> in8, Span<float> out2, int frames)
	{
		ReadOnlySpan<float> left = Left;
		ReadOnlySpan<float> right = Right;
		int f = 0;
		if (Avx.IsSupported)
		{
			// 8 frames at a time: transposed so each vector is one channel across the frames, then the same sum in the same order
			for (; f + 8 <= frames; f += 8)
			{
				ReadOnlySpan<float> block = in8.Slice(f * 8, 64);
				Vector256<float> l = Vector256<float>.Zero;
				Vector256<float> r = Vector256<float>.Zero;
				Transpose(block, out Vector256<float> c0, out Vector256<float> c1, out Vector256<float> c2, out Vector256<float> c3,
					out Vector256<float> c4, out Vector256<float> c5, out Vector256<float> c6, out Vector256<float> c7);
				l += c0 * left[0];
				r += c0 * right[0];
				l += c1 * left[1];
				r += c1 * right[1];
				l += c2 * left[2];
				r += c2 * right[2];
				l += c3 * left[3];
				r += c3 * right[3];
				l += c4 * left[4];
				r += c4 * right[4];
				l += c5 * left[5];
				r += c5 * right[5];
				l += c6 * left[6];
				r += c6 * right[6];
				l += c7 * left[7];
				r += c7 * right[7];

				// back to l, r pairs: [l0 r0 l1 r1 | l4 r4 l5 r5] and [l2 r2 l3 r3 | l6 r6 l7 r7]
				Vector256<float> low = Avx.UnpackLow(l, r);
				Vector256<float> high = Avx.UnpackHigh(l, r);
				Avx.Permute2x128(low, high, 0x20).CopyTo(out2[(f * 2)..]);
				Avx.Permute2x128(low, high, 0x31).CopyTo(out2[((f * 2) + 8)..]);
			}
		}

		for (; f < frames; f++)
		{
			float l = 0f;
			float r = 0f;
			for (int c = 0; c < 8; c++)
			{
				float x = in8[(f * 8) + c];
				l += x * left[c];
				r += x * right[c];
			}

			out2[f * 2] = l;
			out2[(f * 2) + 1] = r;
		}
	}

	// 8 frames x 8 channels to 8 channels x 8 frames
	private static void Transpose(ReadOnlySpan<float> block, out Vector256<float> c0, out Vector256<float> c1, out Vector256<float> c2,
		out Vector256<float> c3, out Vector256<float> c4, out Vector256<float> c5, out Vector256<float> c6, out Vector256<float> c7)
	{
		Vector256<float> t0 = Avx.UnpackLow(Vector256.Create(block[..8]), Vector256.Create(block[8..16]));
		Vector256<float> t1 = Avx.UnpackHigh(Vector256.Create(block[..8]), Vector256.Create(block[8..16]));
		Vector256<float> t2 = Avx.UnpackLow(Vector256.Create(block[16..24]), Vector256.Create(block[24..32]));
		Vector256<float> t3 = Avx.UnpackHigh(Vector256.Create(block[16..24]), Vector256.Create(block[24..32]));
		Vector256<float> t4 = Avx.UnpackLow(Vector256.Create(block[32..40]), Vector256.Create(block[40..48]));
		Vector256<float> t5 = Avx.UnpackHigh(Vector256.Create(block[32..40]), Vector256.Create(block[40..48]));
		Vector256<float> t6 = Avx.UnpackLow(Vector256.Create(block[48..56]), Vector256.Create(block[56..64]));
		Vector256<float> t7 = Avx.UnpackHigh(Vector256.Create(block[48..56]), Vector256.Create(block[56..64]));
		Vector256<float> u0 = Avx.Shuffle(t0, t2, 0x44);
		Vector256<float> u1 = Avx.Shuffle(t0, t2, 0xEE);
		Vector256<float> u2 = Avx.Shuffle(t1, t3, 0x44);
		Vector256<float> u3 = Avx.Shuffle(t1, t3, 0xEE);
		Vector256<float> u4 = Avx.Shuffle(t4, t6, 0x44);
		Vector256<float> u5 = Avx.Shuffle(t4, t6, 0xEE);
		Vector256<float> u6 = Avx.Shuffle(t5, t7, 0x44);
		Vector256<float> u7 = Avx.Shuffle(t5, t7, 0xEE);
		c0 = Avx.Permute2x128(u0, u4, 0x20);
		c1 = Avx.Permute2x128(u1, u5, 0x20);
		c2 = Avx.Permute2x128(u2, u6, 0x20);
		c3 = Avx.Permute2x128(u3, u7, 0x20);
		c4 = Avx.Permute2x128(u0, u4, 0x31);
		c5 = Avx.Permute2x128(u1, u5, 0x31);
		c6 = Avx.Permute2x128(u2, u6, 0x31);
		c7 = Avx.Permute2x128(u3, u7, 0x31);
	}

	// the master process (sub_141EB5DE0): effects (not bypassed, when the master had input or the effect has a tail), then the gain.
	// The vector at master +0xE0 the game also multiplies by is 1.0 with spatial audio off. Returns whether it ran.
	public static bool ApplyMaster(Span<float> buffer, int frames, int channels, ReadOnlySpan<BusEffect> effects, int inputs, float gain)
	{
		bool ran = inputs > 0;
		foreach (BusEffect effect in effects)
		{
			if (effect.Ready && !effect.Skip && (inputs > 0 || effect.Tail))
			{
				effect.Process(buffer, frames, channels);
				ran = true;
			}
		}

		if (ran && gain != 1f)
		{
			Simd.Scale(buffer[..(frames * channels)], gain);
		}

		return ran;
	}

	// spatial audio off: the render callback adds Master ambisonics into Master bed at gain 1 (a MixIn of the bed master)
	public static void AddAmbisonics(Span<float> bed, ReadOnlySpan<float> ambisonics, int length)
	{
		Simd.Add(bed[..length], ambisonics);
	}
}
