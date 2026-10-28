using System;
using System.Buffers.Binary;

namespace SoundBringers.Dsp;

// Read-only copies of the live effects' coefficients (never the filter memory), split in two. Copy runs on the audio thread: it reads
// the game's raw bytes into a blob, touches no effect state, allocates nothing and never throws; it returns the blob length, or -1 when
// the memory cannot be read. LoadCopy runs on the worker and takes the coefficients from the blob; false keeps the previous ones.
// Each Copy takes the reader (GameAudio.TryRead in the plugin) and the object's address (its vtable pointer).
public sealed partial class LowpassEffect
{
	public static int Copy(MemoryRead read, nint gameCore, Span<byte> blob) => read(gameCore, blob[..Size]) ? Size : -1;

	public bool LoadCopy(ReadOnlySpan<byte> blob, out bool skip)
	{
		skip = false;
		if (blob.Length < Size)
		{
			return false;
		}

		skip = blob[EffectLayout.Skip] != 0;
		this.Load(blob, false);
		return true;
	}
}

public sealed partial class EqualizerEffect
{
	public const int CopyBytes = Size + (MaxBands * Biquad.StageBytes);

	// the object header, then the band array behind its pointer
	public static int Copy(MemoryRead read, nint gameEq, Span<byte> blob)
	{
		if (!read(gameEq, blob[..Size]))
		{
			return -1;
		}

		int count = BinaryPrimitives.ReadInt32LittleEndian(blob[CountOffset..]);
		nint bands = (nint)BinaryPrimitives.ReadInt64LittleEndian(blob[BandsOffset..]);
		if (count is < 1 or > MaxBands || bands == 0)
		{
			return -1;
		}

		int length = Size + (count * Biquad.StageBytes);
		return read(bands, blob[Size..length]) ? length : -1;
	}

	public bool LoadCopy(ReadOnlySpan<byte> blob, out bool skip)
	{
		skip = false;
		if (blob.Length < Size)
		{
			return false;
		}

		int count = BinaryPrimitives.ReadInt32LittleEndian(blob[CountOffset..]);
		if (count is < 1 or > MaxBands || blob.Length < Size + (count * Biquad.StageBytes))
		{
			return false;
		}

		skip = blob[EffectLayout.Skip] != 0;
		this.Load(blob[Size..(Size + (count * Biquad.StageBytes))], false);
		return true;
	}
}

public sealed partial class MasterEqualizerEffect
{
	public static int Copy(MemoryRead read, nint gameEq, Span<byte> blob) => read(gameEq, blob[..Size]) ? Size : -1;

	public bool LoadCopy(ReadOnlySpan<byte> blob, out bool skip)
	{
		skip = false;
		if (blob.Length < Size)
		{
			return false;
		}

		skip = blob[EffectLayout.Skip] != 0;
		this.Load(blob, false);
		return true;
	}
}

public sealed partial class WahWahEffect
{
	public static int Copy(MemoryRead read, nint gameCore, Span<byte> blob) => read(gameCore, blob[..Size]) ? Size : -1;

	public bool LoadCopy(ReadOnlySpan<byte> blob, out bool skip)
	{
		skip = false;
		if (blob.Length < Size)
		{
			return false;
		}

		skip = blob[EffectLayout.Skip] != 0;
		this.Load(blob, false);
		return true;
	}
}

public sealed partial class FaithReverbEffect
{
	public const int CopyBytes = Size + (2 * GainBytes);

	// the object, then the early-reflection gains and tap indices behind its two pointers
	public static int Copy(MemoryRead read, nint gameCore, Span<byte> blob)
	{
		if (!read(gameCore, blob[..Size]))
		{
			return -1;
		}

		nint gainPointer = (nint)BinaryPrimitives.ReadInt64LittleEndian(blob[GainsPointer..]);
		nint indexPointer = (nint)BinaryPrimitives.ReadInt64LittleEndian(blob[IndicesPointer..]);
		return gainPointer != 0
			&& indexPointer != 0
			&& read(gainPointer, blob.Slice(Size, GainBytes))
			&& read(indexPointer, blob.Slice(Size + GainBytes, GainBytes))
				? CopyBytes
				: -1;
	}

	public bool LoadCopy(ReadOnlySpan<byte> blob, out bool skip)
	{
		skip = false;
		if (blob.Length < CopyBytes)
		{
			return false;
		}

		skip = blob[EffectLayout.Skip] != 0;
		return this.Load(blob[..Size], blob.Slice(Size, GainBytes), blob.Slice(Size + GainBytes, GainBytes), false);
	}
}

public sealed partial class StringendoEffect
{
	public const int CopyBytes = TailBytes + 1;

	// the object's tail, then its bypass flag
	public static int Copy(MemoryRead read, nint gameCore, Span<byte> blob) =>
		read(gameCore + TailOffset, blob[..TailBytes]) && read(gameCore + EffectLayout.Skip, blob.Slice(TailBytes, 1)) ? CopyBytes : -1;

	public bool LoadCopy(ReadOnlySpan<byte> blob, out bool skip)
	{
		skip = false;
		if (blob.Length < CopyBytes)
		{
			return false;
		}

		skip = blob[TailBytes] != 0;
		this.Load(blob[..TailBytes]);
		return true;
	}
}

public sealed partial class VoiceFilter
{
	public const int Size = 0xD0;

	public static int Copy(MemoryRead read, nint gameFilter, Span<byte> blob) => read(gameFilter, blob[..Size]) ? Size : -1;

	// the skip flag and coefficients of the voice's filter object (never its memory)
	public bool LoadCopy(ReadOnlySpan<byte> core)
	{
		if (core.Length < Size)
		{
			return false;
		}

		// not a filter this plugin knows (a recycled object): coefficients that are not finite or not stable are never run
		float newA = BinaryPrimitives.ReadSingleLittleEndian(core[80..]);
		float newB = BinaryPrimitives.ReadSingleLittleEndian(core[84..]);
		if (!float.IsFinite(newA) || !float.IsFinite(newB) || MathF.Abs(newA) >= 1f)
		{
			this.skip = true;
			return false;
		}

		this.skip = core[EffectLayout.Skip] != 0;
		this.a = newA;
		this.b = newB;
		return true;
	}
}
