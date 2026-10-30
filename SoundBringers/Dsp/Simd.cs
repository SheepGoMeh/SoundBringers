using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SoundBringers.Dsp;

// the mixer's element-wise loops, Vector<float> wide with a scalar tail. Same operations in the same order per element as the scalar
// loops (the JIT does not fuse multiply-add), so the output is bit for bit the same.
public static class Simd
{
	// dst = (src x gain) + dst
	public static void MulAdd(Span<float> dst, ReadOnlySpan<float> src, float gain)
	{
		Span<Vector<float>> d = MemoryMarshal.Cast<float, Vector<float>>(dst);
		ReadOnlySpan<Vector<float>> s = MemoryMarshal.Cast<float, Vector<float>>(src[..dst.Length]);
		Vector<float> g = new(gain);
		for (int i = 0; i < d.Length; i++)
		{
			d[i] = (s[i] * g) + d[i];
		}

		for (int i = d.Length * Vector<float>.Count; i < dst.Length; i++)
		{
			dst[i] = (src[i] * gain) + dst[i];
		}
	}

	// dst = src + dst
	public static void Add(Span<float> dst, ReadOnlySpan<float> src)
	{
		Span<Vector<float>> d = MemoryMarshal.Cast<float, Vector<float>>(dst);
		ReadOnlySpan<Vector<float>> s = MemoryMarshal.Cast<float, Vector<float>>(src[..dst.Length]);
		for (int i = 0; i < d.Length; i++)
		{
			d[i] = s[i] + d[i];
		}

		for (int i = d.Length * Vector<float>.Count; i < dst.Length; i++)
		{
			dst[i] = src[i] + dst[i];
		}
	}

	// dst *= gain
	public static void Scale(Span<float> dst, float gain)
	{
		Span<Vector<float>> d = MemoryMarshal.Cast<float, Vector<float>>(dst);
		Vector<float> g = new(gain);
		for (int i = 0; i < d.Length; i++)
		{
			d[i] *= g;
		}

		for (int i = d.Length * Vector<float>.Count; i < dst.Length; i++)
		{
			dst[i] *= gain;
		}
	}

	// x - x is 0 for a finite x, NaN for an infinity or a NaN
	public static bool Finite(ReadOnlySpan<float> samples)
	{
		ReadOnlySpan<Vector<float>> v = MemoryMarshal.Cast<float, Vector<float>>(samples);
		for (int i = 0; i < v.Length; i++)
		{
			if (!Vector.EqualsAll(v[i] - v[i], Vector<float>.Zero))
			{
				return false;
			}
		}

		for (int i = v.Length * Vector<float>.Count; i < samples.Length; i++)
		{
			if (!float.IsFinite(samples[i]))
			{
				return false;
			}
		}

		return true;
	}
}
