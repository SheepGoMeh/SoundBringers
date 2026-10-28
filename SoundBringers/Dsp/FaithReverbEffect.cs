using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SoundBringers.Dsp;

// the game's reverb core effect (SQEX::Sd::Driver::FaithReverb, 0x10C0-byte object, process 0x141EA6350). Per frame: input folded to
// 4 lanes (8, 16 or 2 channels), four band-pass biquads (4 lanes), a predelay, 49 x 4 early-reflection taps, a mix with the early
// reflections (sin/cos), a second delay, a 4-line feedback network (the gain matrix, a 16 x 16 Hadamard), then output =
// input x dry + early x wet x 0.7071 x early level + late x wet x 0.7071. Eight channels fill all eight, 16 channels only the
// first four, other counts (2) run the network and write nothing, as the game does.
// The object holds the derived coefficients (the game recomputes them when a parameter fades, which this port does not,
// except for the dry, wet, mix and gain values); pointers hold the delay lines. Offsets below are from the object start.
public sealed partial class FaithReverbEffect
{
	public const int Size = 0x10C0;
	public const int GainBytes = 784; // 196 early-reflection gains behind the pointer at 0xA00, and as many tap indices at 0xA08
	public const float SampleRate = 48000f; // the mixer's rate: the line sizes are fractions of it (0.2 s -> 9600 vectors)

	public const int GainsPointer = 0xA00;
	public const int IndicesPointer = 0xA08;

	// delay lines of 16-byte vectors: key, pointer offset, count offset (the four sub-lines and the four network lines share one)
	public static readonly (string Key, int Pointer, int Count)[] Lines =
	[
		("taps", 0x9F0, 0x9F8),
		("predelay", 0xA20, 0xA28),
		("line", 0xA40, 0xA48),
		("sub0", 0xA60, 0xA80),
		("sub1", 0xA68, 0xA80),
		("sub2", 0xA70, 0xA80),
		("sub3", 0xA78, 0xA80),
		("fdn0", 0xB10, 0xB30),
		("fdn1", 0xB18, 0xB30),
		("fdn2", 0xB20, 0xB30),
		("fdn3", 0xB28, 0xB30),
	];

	public const int TapWrite = 0xA10;
	public const int PredelayWrite = 0xA2C;
	public const int PredelayRead = 0xA30;
	public const int LineWrite = 0xA4C;
	public const int LineRead = 0xA50;
	public const int SubWrite = 0xA84;
	public const int SubIndices = 0xA90; // 4 lines x 4 lanes
	public const int SubMemory = 0xAD0; // 4 vectors: the previous gather of each sub-line
	public const int FdnWrite = 0xBA0;
	public const int FdnIndices = 0xB60;
	public const int FdnFeedback = 0xB40; // 4 lanes
	public const int FdnInput = 0xB50; // 4 lanes
	public const int GainMatrix = 0xBB0; // 16 floats
	public const int GainDamping = 0xBF0; // 16 floats
	public const int GainMemory = 0xC30; // 16 floats: the last result
	public const int DryOffset = 0x40;
	public const int WetOffset = 0x80;
	public const int SinOffset = 0xC0;
	public const int CosOffset = 0xD0;
	public const int GainOffset = 0xE0;
	public const int DryFade = 0xC70; // fading values, 48 bytes each: start +8, end +12, time +16, duration +20, curve +24, fading +28
	public const int WetFade = 0xCA0;
	public const int MixFade = 0xCD0;
	public const int EarlyFade = 0xD30;
	public const int GainFade = 0xDC0;

	private const int StagesOffset = 0xF0;
	private const int StageSpacing = Biquad.StageBytes; // stages at 0xF0, 0x330, 0x570, 0x7B0
	private const int StageCount = 4;
	private const int Taps = 196;
	private const int Sub = 3;
	private const int Fdn = 7;
	private const float Half = 0.70710677f;
	private const float AmbiGain = 1.7782794f;
	private const float Quarter = 0.25f;
	private const float HalfPi = 1.5707964f;

	// the game applies the stages in this order (objects 0xF0, 0x570, 0x7B0, 0x330)
	private static readonly int[] StageOrder = [0, 2, 3, 1];

	// 16 x 16 Hadamard as the game's table: sign of lane (4q + k) in row m is (-1)^popcount(m & (4q + k))
	private static readonly float[] Hadamard = BuildHadamard();
	private static readonly int[] Expected = ExpectedCounts();

	private readonly BiquadState[] stages = new BiquadState[StageCount];
	private readonly float[][] lines = new float[Lines.Length][];
	private readonly int[] counts = Expected;
	private readonly float[] gains = new float[Taps];
	private readonly int[] tapIndices = new int[Taps];
	private readonly int[] subIndices = new int[16];
	private readonly int[] fdnIndices = new int[16];
	private readonly float[] memory = new float[16]; // SubMemory
	private readonly float[] previous = new float[16]; // GainMemory
	private readonly float[] matrix = new float[16];
	private readonly float[] damping = new float[16];
	private readonly float[] feedback = new float[4];
	private readonly float[] input = new float[4];
	private readonly Fade dry = new();
	private readonly Fade wet = new();
	private readonly Fade mix = new();
	private readonly Fade early = new();
	private readonly Fade gain = new();

	// per-frame scratch, so Process allocates nothing
	private readonly float[] x = new float[4];
	private readonly float[] reflections = new float[4];
	private readonly float[] delayed = new float[4];
	private readonly float[] sent = new float[4];
	private readonly float[] returned = new float[16];
	private readonly float[] feeds = new float[16];
	private readonly float[] late = new float[16];
	private readonly float[] results = new float[16];
	private readonly float[] gather = new float[4];
	private readonly float[] feedValue = new float[4];

	private bool ready;
	private float dryValue;
	private float wetValue;
	private float sinValue;
	private float cosValue;
	private float gainValue;
	private int tapWrite;
	private int predelayWrite;
	private int predelayRead;
	private int lineWrite;
	private int lineRead;
	private int subWrite;
	private int fdnWrite;

	public bool Ready => this.ready;

	// the delay lines, in the order of Lines
	public float[][] DelayLines => this.lines;

	// every index and write position, in a fixed order
	public int[] Positions =>
	[
		.. this.tapIndices,
		this.tapWrite,
		this.predelayWrite,
		this.predelayRead,
		this.lineWrite,
		this.lineRead,
		.. this.subIndices,
		this.subWrite,
		.. this.fdnIndices,
		this.fdnWrite,
	];

	// filter memory of the four stages, then the sub-line memory and the last gain-matrix result
	public float[] State
	{
		get
		{
			float[] all = new float[(StageCount * Biquad.StateFloats) + 32];
			for (int i = 0; i < StageCount; i++)
			{
				((ReadOnlySpan<float>)this.stages[i])[Biquad.CoeffFloats..].CopyTo(all.AsSpan(i * Biquad.StateFloats));
			}

			this.memory.CopyTo(all, StageCount * Biquad.StateFloats);
			this.previous.CopyTo(all, (StageCount * Biquad.StateFloats) + 16);
			return all;
		}
	}

	// vectors per line, as the constructor (sub_141EA4A10) sizes them from the rate alone: x0.39, 0.2, 0.3, 4 x 0.3, 4 x 0.035
	private static int[] ExpectedCounts()
	{
		float[] factors = [0.39000002f, 0.2f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f, 0.035f, 0.035f, 0.035f, 0.035f];
		int[] counts = new int[factors.Length];
		for (int i = 0; i < counts.Length; i++)
		{
			counts[i] = (int)(SampleRate * factors[i]);
		}

		return counts;
	}

	public static int ExpectedLineCount(string key) => Expected[Array.FindIndex(Lines, l => l.Key == key)];

	public FaithReverbEffect()
	{
		for (int i = 0; i < Lines.Length; i++)
		{
			this.lines[i] = new float[this.counts[i] * 4];
		}
	}

	private static float[] BuildHadamard()
	{
		float[] table = new float[256];
		for (int m = 0; m < 16; m++)
		{
			for (int c = 0; c < 16; c++)
			{
				table[(m * 16) + c] = (BitOperations.PopCount((uint)(m & c)) & 1) == 0 ? 1f : -1f;
			}
		}

		return table;
	}

	private static int Count(ReadOnlySpan<byte> core, int offset) => BinaryPrimitives.ReadInt32LittleEndian(core[offset..]);

	private static int Clamp(int index, int size) => (uint)index < (uint)size ? index : 0;

	// an index kept at the same distance behind the write position as the game's
	private static int Behind(int gameIndex, int gameWrite, int size, int write)
	{
		int distance = Clamp(gameWrite, size) - Clamp(gameIndex, size);
		if (distance < 0)
		{
			distance += size;
		}

		int index = write - distance;
		return index < 0 ? index + size : index;
	}

	// Parameters, coefficients and sizes from the game's object and the two 784-byte blocks behind it. withState also takes the game's
	// positions, filter memory and indices; otherwise indices keep the game's distance behind this copy's write position. The lines are
	// sized once, from the rate, as the game's; any other size returns false and changes nothing.
	// The game's reset (vf1, 0x141EA5540) is followed when seen: all write positions and memory blocks at zero clear this copy too. A
	// reset seen late is missed and the old tail plays out.
	public bool Load(ReadOnlySpan<byte> core, ReadOnlySpan<byte> gainBlock, ReadOnlySpan<byte> indexBlock, bool withState)
	{
		if (core.Length < Size || gainBlock.Length < GainBytes || indexBlock.Length < GainBytes)
		{
			return false;
		}

		for (int i = 0; i < Lines.Length; i++)
		{
			if (Count(core, Lines[i].Count) != this.counts[i])
			{
				return false;
			}
		}

		if (!withState && this.GameReset(core))
		{
			foreach (float[] line in this.lines)
			{
				line.AsSpan().Clear();
			}

			this.tapWrite = this.predelayWrite = this.predelayRead = this.lineWrite = this.lineRead = this.subWrite = this.fdnWrite = 0;
			this.memory.AsSpan().Clear();
			this.previous.AsSpan().Clear();
		}

		for (int i = 0; i < StageCount; i++)
		{
			Biquad.Load(core[(StagesOffset + (i * StageSpacing))..], this.stages[i], withState);
		}

		MemoryMarshal.Cast<byte, float>(gainBlock[..GainBytes]).CopyTo(this.gains);
		MemoryMarshal.Cast<byte, float>(core.Slice(GainMatrix, 64)).CopyTo(this.matrix);
		MemoryMarshal.Cast<byte, float>(core.Slice(GainDamping, 64)).CopyTo(this.damping);
		MemoryMarshal.Cast<byte, float>(core.Slice(FdnFeedback, 16)).CopyTo(this.feedback);
		MemoryMarshal.Cast<byte, float>(core.Slice(FdnInput, 16)).CopyTo(this.input);
		this.dryValue = BitConverter.ToSingle(core[DryOffset..]);
		this.wetValue = BitConverter.ToSingle(core[WetOffset..]);
		this.sinValue = BitConverter.ToSingle(core[SinOffset..]);
		this.cosValue = BitConverter.ToSingle(core[CosOffset..]);
		this.gainValue = BitConverter.ToSingle(core[GainOffset..]);
		this.dry.Read(core, DryFade);
		this.wet.Read(core, WetFade);
		this.mix.Read(core, MixFade);
		this.early.Read(core, EarlyFade);
		this.gain.Read(core, GainFade);

		int tapSize = this.counts[0];
		int predelaySize = this.counts[1];
		int lineSize = this.counts[2];
		int subSize = this.counts[Sub];
		int fdnSize = this.counts[Fdn];
		ReadOnlySpan<int> tapGame = MemoryMarshal.Cast<byte, int>(indexBlock[..GainBytes]);
		if (withState)
		{
			this.tapWrite = Clamp(Count(core, TapWrite), tapSize);
			this.predelayWrite = Clamp(Count(core, PredelayWrite), predelaySize);
			this.predelayRead = Clamp(Count(core, PredelayRead), predelaySize);
			this.lineWrite = Clamp(Count(core, LineWrite), lineSize);
			this.lineRead = Clamp(Count(core, LineRead), lineSize);
			this.subWrite = Clamp(Count(core, SubWrite), subSize);
			this.fdnWrite = Clamp(Count(core, FdnWrite), fdnSize);
			for (int i = 0; i < Taps; i++)
			{
				this.tapIndices[i] = Clamp(tapGame[i], tapSize);
			}

			for (int i = 0; i < 16; i++)
			{
				this.subIndices[i] = Clamp(Count(core, SubIndices + (i * 4)), subSize);
				this.fdnIndices[i] = Clamp(Count(core, FdnIndices + (i * 4)), fdnSize);
			}

			MemoryMarshal.Cast<byte, float>(core.Slice(SubMemory, 64)).CopyTo(this.memory);
			MemoryMarshal.Cast<byte, float>(core.Slice(GainMemory, 64)).CopyTo(this.previous);
		}
		else
		{
			// the read positions of the predelay and the second line, as the same distance behind this copy's write
			this.predelayRead = Behind(Count(core, PredelayRead), Count(core, PredelayWrite), predelaySize, this.predelayWrite);
			this.lineRead = Behind(Count(core, LineRead), Count(core, LineWrite), lineSize, this.lineWrite);
			int tapGameWrite = Count(core, TapWrite);
			int subGameWrite = Count(core, SubWrite);
			int fdnGameWrite = Count(core, FdnWrite);
			for (int i = 0; i < Taps; i++)
			{
				this.tapIndices[i] = Behind(tapGame[i], tapGameWrite, tapSize, this.tapWrite);
			}

			for (int i = 0; i < 16; i++)
			{
				this.subIndices[i] = Behind(Count(core, SubIndices + (i * 4)), subGameWrite, subSize, this.subWrite);
				this.fdnIndices[i] = Behind(Count(core, FdnIndices + (i * 4)), fdnGameWrite, fdnSize, this.fdnWrite);
			}
		}

		this.ready = true;
		return true;
	}

	private bool GameReset(ReadOnlySpan<byte> core) =>
		Count(core, TapWrite) == 0
		&& Count(core, PredelayWrite) == 0
		&& Count(core, LineWrite) == 0
		&& Count(core, SubWrite) == 0
		&& Count(core, FdnWrite) == 0
		&& !core.Slice(SubMemory, 64).ContainsAnyExcept((byte)0)
		&& !core.Slice(GainMemory, 64).ContainsAnyExcept((byte)0);

	public void Process(Span<float> buf, int frames, int channels)
	{
		if (!this.ready || channels is not (2 or 8 or 16))
		{
			return;
		}

		this.Advance((float)frames / SampleRate);
		float earlyLevel = this.early.Value();
		float lateScale = this.wetValue * Half;
		float earlyScale = lateScale * earlyLevel;
		float[] l = this.late;
		Span<float> w = stackalloc float[8];
		Span<float> v = stackalloc float[8];
		for (int f = 0; f < frames; f++)
		{
			Span<float> frame = buf.Slice(f * channels, channels);
			this.Fold(frame, channels);
			this.Tick();
			if (channels == 8)
			{
				// early reflections and the late result spread over the eight channels (the third and fourth are the input times dry)
				float e0 = this.reflections[0];
				float e1 = this.reflections[1];
				float e2 = this.reflections[2];
				float e3 = this.reflections[3];
				w[0] = e0;
				w[1] = e1;
				w[2] = 0f;
				w[3] = 0f;
				w[4] = e2 + e0;
				w[5] = e3 + e1;
				w[6] = e2;
				w[7] = e3;
				v[0] = l[1];
				v[1] = l[2];
				v[2] = 0f;
				v[3] = 0f;
				v[4] = l[1] + l[0];
				v[5] = l[3] + l[2];
				v[6] = l[0];
				v[7] = l[3];
				for (int c = 0; c < 8; c++)
				{
					frame[c] = (v[c] * lateScale) + ((w[c] * earlyScale) + (frame[c] * this.dryValue));
				}
			}
			else if (channels == 16)
			{
				// only the first four channels (first-order ambisonics) are touched
				v[0] = l[1];
				v[1] = l[2];
				v[2] = l[0];
				v[3] = l[3];
				for (int c = 0; c < 4; c++)
				{
					frame[c] = (v[c] * lateScale) + ((this.reflections[c] * earlyScale) + (frame[c] * this.dryValue));
				}
			}
		}
	}

	// the fading values are stepped once per period (frames / rate); only the ones that move after setup are followed
	private void Advance(float dt)
	{
		if (this.gain.Fading)
		{
			this.gain.Step(dt);
			this.gainValue = this.gain.Value();
		}

		if (this.dry.Fading)
		{
			this.dry.Step(dt);
			this.dryValue = this.dry.Value();
		}

		if (this.wet.Fading)
		{
			this.wet.Step(dt);
			this.wetValue = this.wet.Value();
		}

		if (this.mix.Fading)
		{
			this.mix.Step(dt);
			this.sinValue = MathF.Sin(this.mix.Value() * HalfPi);
			this.cosValue = MathF.Cos(this.mix.Value() * HalfPi);
		}
	}

	private void Fold(ReadOnlySpan<float> frame, int channels)
	{
		float[] x = this.x;
		switch (channels)
		{
			case 8:
				x[0] = ((frame[2] * Half) + frame[0]) + (frame[4] * Half);
				x[1] = ((frame[2] * Half) + frame[1]) + (frame[5] * Half);
				x[2] = (frame[4] * Half) + frame[6];
				x[3] = (frame[5] * Half) + frame[7];
				break;
			case 16:
				x[0] = frame[0] * AmbiGain;
				x[1] = frame[1];
				x[2] = frame[2];
				x[3] = frame[3];
				break;
			default:
				x[0] = frame[0] * Half;
				x[1] = frame[1] * Half;
				x[2] = frame[0] * Half;
				x[3] = frame[1] * Half;
				break;
		}
	}

	// one frame of the network: x in, early reflections to this.reflections, the late result to this.late
	private void Tick()
	{
		float[] x = this.x;
		foreach (int s in StageOrder)
		{
			Span<float> stage = this.stages[s];
			Biquad.ProcessDf1(x, 1, 4, stage[..Biquad.CoeffFloats], stage[Biquad.CoeffFloats..], false, true, true, true);
		}

		// predelay: read the oldest vector, then write the new one
		float[] pre = this.lines[1];
		for (int k = 0; k < 4; k++)
		{
			this.delayed[k] = pre[(this.predelayRead * 4) + k];
			pre[(this.predelayWrite * 4) + k] = x[k];
		}

		if (++this.predelayRead >= this.counts[1])
		{
			this.predelayRead = 0;
		}

		if (++this.predelayWrite >= this.counts[1])
		{
			this.predelayWrite = 0;
		}

		// early reflections: lane k of every tap reads component k of its vector in the tap line
		float[] taps = this.lines[0];
		int tapSize = this.counts[0];
		float[] r = this.reflections;
		r.AsSpan().Clear();
		for (int i = 0; i < Taps; i += 4)
		{
			for (int k = 0; k < 4; k++)
			{
				float product = taps[(this.tapIndices[i + k] * 4) + k] * this.gains[i + k];
				r[k] += product;
				if (++this.tapIndices[i + k] > tapSize - 1)
				{
					this.tapIndices[i + k] = 0;
				}
			}
		}

		for (int k = 0; k < 4; k++)
		{
			taps[(this.tapWrite * 4) + k] = this.delayed[k];
		}

		if (++this.tapWrite >= tapSize)
		{
			this.tapWrite = 0;
		}

		// early and direct mixed, through the second line, then the four feed vectors (plus and minus the line output)
		float[] line = this.lines[2];
		for (int k = 0; k < 4; k++)
		{
			float feedIn = (x[k] * this.cosValue) + (r[k] * this.sinValue);
			this.sent[k] = line[(this.lineRead * 4) + k];
			line[(this.lineWrite * 4) + k] = feedIn;
		}

		if (++this.lineRead >= this.counts[2])
		{
			this.lineRead = 0;
		}

		if (++this.lineWrite >= this.counts[2])
		{
			this.lineWrite = 0;
		}

		for (int k = 0; k < 4; k++)
		{
			this.feeds[k] = (this.sent[k] * this.gainValue) + this.memory[k];
			this.feeds[4 + k] = (-this.sent[k] * this.gainValue) + this.memory[4 + k];
			this.feeds[8 + k] = (this.sent[k] * this.gainValue) + this.memory[8 + k];
			this.feeds[12 + k] = (-this.sent[k] * this.gainValue) + this.memory[12 + k];
		}

		// the four network lines
		int fdnSize = this.counts[Fdn];
		for (int j = 0; j < 4; j++)
		{
			float[] line4 = this.lines[Fdn + j];
			for (int k = 0; k < 4; k++)
			{
				this.gather[k] = line4[(this.fdnIndices[(j * 4) + k] * 4) + k];
			}

			for (int k = 0; k < 4; k++)
			{
				float fed = (this.gather[k] * this.input[k]) + this.feeds[(j * 4) + k];
				this.returned[(j * 4) + k] = (fed * this.feedback[k]) + this.gather[k];
				this.feedValue[k] = Biquad.Flush(fed);
			}

			for (int k = 0; k < 4; k++)
			{
				line4[(this.fdnWrite * 4) + k] = this.feedValue[k];
				if (++this.fdnIndices[(j * 4) + k] >= fdnSize)
				{
					this.fdnIndices[(j * 4) + k] = 0;
				}
			}
		}

		if (++this.fdnWrite >= fdnSize)
		{
			this.fdnWrite = 0;
		}

		// 16 x 16 Hadamard over the four returns, scaled by 1/4; each output is the sum of its four lanes
		float[] h = Hadamard;
		for (int m = 0; m < 16; m++)
		{
			float[] lane = this.gather;
			for (int k = 0; k < 4; k++)
			{
				float a = (h[(m * 16) + k] * this.returned[k]);
				float b = (h[(m * 16) + 4 + k] * this.returned[4 + k]) + a;
				float c = (h[(m * 16) + 8 + k] * this.returned[8 + k]) + b;
				float d = (h[(m * 16) + 12 + k] * this.returned[12 + k]) + c;
				lane[k] = d * Quarter;
			}

			this.late[m] = (lane[0] + lane[1]) + (lane[2] + lane[3]);
		}

		// damping and gain per lane, flushed, kept for the next frame
		for (int i = 0; i < 16; i++)
		{
			this.results[i] = Biquad.Flush((this.matrix[i] * this.late[i]) - (this.damping[i] * this.previous[i]));
			this.previous[i] = this.results[i];
		}

		// the four sub-lines: read the lane diagonal into the memory, then write the result vector
		int subSize = this.counts[Sub];
		for (int j = 0; j < 4; j++)
		{
			float[] subLine = this.lines[Sub + j];
			for (int k = 0; k < 4; k++)
			{
				this.memory[(j * 4) + k] = subLine[(this.subIndices[(j * 4) + k] * 4) + k];
				subLine[(this.subWrite * 4) + k] = this.results[(j * 4) + k];
				if (++this.subIndices[(j * 4) + k] >= subSize)
				{
					this.subIndices[(j * 4) + k] = 0;
				}
			}
		}

		if (++this.subWrite >= subSize)
		{
			this.subWrite = 0;
		}
	}

	// a DynamicValue<2> as the game lays it out: 48 bytes, the value moves from start to end over duration along a curve
	internal sealed class Fade
	{
		private float start;
		private float end;
		private float time;
		private float duration;
		private int curve;

		public bool Fading { get; private set; }

		public void Read(ReadOnlySpan<byte> core, int offset)
		{
			this.start = BitConverter.ToSingle(core[(offset + 8)..]);
			this.end = BitConverter.ToSingle(core[(offset + 12)..]);
			this.time = BitConverter.ToSingle(core[(offset + 16)..]);
			this.duration = BitConverter.ToSingle(core[(offset + 20)..]);
			this.curve = Count(core, offset + 24);
			this.Fading = core[offset + 28] != 0;
		}

		// sub_141EA7ED0
		public float Value()
		{
			if (this.duration == 0f)
			{
				return this.end;
			}

			if (this.time < 0f)
			{
				return this.start;
			}

			float t = this.time / this.duration;
			float shaped = this.curve switch
			{
				0 => t,
				1 => 1f - Cube(1f - (t * t)),
				2 => 1f - ((1f - t) * (1f - t)),
				3 => t * t,
				4 => MathF.Pow(2f, t) - 1f,
				5 => 2f - MathF.Pow(2f, 1f - t),
				_ => 0f,
			};
			return (shaped * (this.end - this.start)) + this.start;
		}

		// sub_141EA8290
		public void Step(float dt)
		{
			this.time = dt + this.time;
			if (this.time < this.duration)
			{
				this.Fading = true;
			}
			else
			{
				this.time = this.duration;
				this.Fading = false;
			}
		}

		private static float Cube(float a) => a * a * a;
	}
}
