using System;
using System.Buffers.Binary;

namespace SoundBringers.Dsp;

// terms of the game's per-voice volume, each as the game reads them (dynamic values already as Current x Scale):
// RootSound (sub_141E8EFD0), then the child sound (sub_141E898F0), then the VoiceSead (+0x2C x +0x30)
public struct SoundTerms
{
	public const int SeadBytes = 0x34;
	public const int ChildBytes = 0x240;
	public const int RootBytes = 0x480;

	private const int DynamicCurrent = 0x08;
	private const int DynamicScale = 0x20;
	private const int DynamicMode = 0x25; // nonzero: the value is modulated by a random generator, not just Current

	public float Dv458;
	public float BusCurrent; // RootSound +0x38 Current; its Scale is the bus fader product
	public float Dv160;
	public float DvD8;
	public float F12C;
	public float F130;
	public float Scalar; // Self/Party/Other balance, 1 when it does not apply
	public float ChildDv128;
	public float ChildDv88Current; // child +0x88 Scale is the RootSound volume
	public float F23C;
	public float F220;
	public float SeadFactor; // VoiceSead +0x2C
	public bool Modulated;

	public static SoundTerms From(ReadOnlySpan<byte> sead, ReadOnlySpan<byte> child, ReadOnlySpan<byte> root, float scalar)
	{
		bool modulated = false;
		float Value(ReadOnlySpan<byte> block, int offset)
		{
			modulated |= block[offset + DynamicMode] != 0;
			return Float(block, offset + DynamicCurrent) * Float(block, offset + DynamicScale);
		}

		return new SoundTerms
		{
			Dv458 = Value(root, 0x458),
			BusCurrent = Float(root, 0x38 + DynamicCurrent),
			Dv160 = Value(root, 0x160),
			DvD8 = Value(root, 0xD8),
			F12C = Float(root, 0x12C),
			F130 = Float(root, 0x130),
			Scalar = scalar,
			ChildDv128 = Value(child, 0x128),
			ChildDv88Current = Float(child, 0x88 + DynamicCurrent),
			F23C = Float(child, 0x23C),
			F220 = Float(child, 0x220),
			SeadFactor = Float(sead, 0x2C),
			Modulated = modulated,
		};
	}

	private static float Float(ReadOnlySpan<byte> block, int offset) => BinaryPrimitives.ReadSingleLittleEndian(block[offset..]);
}

public static class VolumeChain
{
	// the Self/Party/Other factor: only for sounds with state flag 0x10, outside the mode that skips it
	public static float Scalar(uint flags, int playerCategory, int mode, float player, float party, float other) =>
		mode == 1 || (flags & 0x10) == 0
			? 1f
			: playerCategory switch { 0 => player, 1 => party, 3 => other, _ => 1f };

	// a bus fader (Bus +0x18/+0x40/+0x68/+0x90) is Current x Scale
	public static float Fader(ReadOnlySpan<byte> bus, int offset) =>
		BinaryPrimitives.ReadSingleLittleEndian(bus[(offset + 0x08)..]) * BinaryPrimitives.ReadSingleLittleEndian(bus[(offset + 0x20)..]);

	// the game's first fader (SoundManager.GetEffectiveVolume) is ((factor2 x slider) x factor3 x factor4 x factor5) x category mute x window-inactive mute;
	// the stream swaps the slider for the level and leaves the user's mutes out, keeping the four factors the game drives itself
	public static float Fader1(float level, float factor2, float factor3, float factor4, float factor5) =>
		(((factor2 * level) * factor3) * factor4) * factor5;

	// operation order as in the game; faders 2-4 are the ducking faders (the game multiplies f3 x (f1 x f2) x f4)
	public static float Compose(in SoundTerms t, float fader1, float fader2, float fader3, float fader4)
	{
		float bus = (fader3 * (fader1 * fader2)) * fader4;

		// RootSound volume
		float root = t.BusCurrent * bus;
		root *= t.Dv458;
		root *= t.F12C;
		root *= t.F130;
		root = t.Dv160 * root;
		root = t.DvD8 * root;
		root *= t.Scalar;

		// child sound volume; the curve (sub_141EF9BA0) is the identity in the shipped config (VOLUME_CURVE OLD, mode 3), bar the clamp at 0 and 1
		float child = (t.ChildDv88Current * root) * t.ChildDv128;
		child *= t.F23C;
		child = child >= 1f ? 1f : child <= 0f ? 0f : child;
		return (child * t.F220) * t.SeadFactor;
	}
}
