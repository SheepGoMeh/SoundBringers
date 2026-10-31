using System;
using System.Collections.Generic;

using SoundBringers.Mix;

namespace SoundBringers.Dsp;

// one submix or master of the shadow mixer: an accumulator the voices and other nodes mix into, its effects, its sends
public sealed class ShadowNode(nint pointer, bool master, int[] sendTo, float[] sendGain, BusEffect[] effects, int length)
{
	public nint Pointer => pointer;

	public bool Master => master;

	public int[] SendTo => sendTo;

	public float[] SendGain => sendGain;

	public BusEffect[] Effects => effects;

	public readonly float[] Buffer = new float[length];

	public int Inputs;
}

// The plugin's copy of the game's mix graph for one period (spatial audio off): voices mix into submix accumulators, the submixes
// run in send order, the masters add up, the result is scaled and folded to stereo. Everything is allocated here, on the framework
// thread; Add and Process allocate nothing.
public sealed class ShadowGraph
{
	private readonly ShadowNode[] nodes;
	private readonly int[] order;
	private readonly int bed;
	private readonly int ambisonics;

	// bed / ambisonics: node indices of Master bed and Master ambisonics, -1 when absent
	public ShadowGraph(ShadowNode[] nodes, int bed, int ambisonics, int frames, int channels, ShadowGraph[]? splits = null)
	{
		this.Splits = splits;
		this.nodes = nodes;
		this.bed = bed;
		this.ambisonics = ambisonics;
		this.Frames = frames;
		this.Channels = channels;
		this.Scratch = new float[frames * channels];
		this.Output = new float[frames * 2];
		this.order = GraphOrder.Order(Array.ConvertAll(nodes, n => n.SendTo));
		foreach (ShadowNode node in nodes)
		{
			foreach (BusEffect effect in node.Effects)
			{
				this.EffectCount++;
			}
		}
	}

	// one period's voice render and stereo result, allocated with the graph
	public readonly float[] Scratch;
	public readonly float[] Output;

	// one graph per stream category with the same nodes and effects of its own, when the stream is split; this graph then only
	// lends its layout and its scratch, and does not process
	public ShadowGraph[]? Splits { get; }

	public int Frames { get; }

	public int Channels { get; }

	public int EffectCount { get; }

	// audio thread: the raw bytes of every effect, in node order, into the slot. false when they do not fit (the period is dropped).
	internal bool CopyEffects(PeriodSlot slot)
	{
		slot.EffectCount = 0;
		if (this.EffectCount > slot.EffectCapacity)
		{
			return false;
		}

		foreach (ShadowNode node in this.nodes)
		{
			foreach (BusEffect effect in node.Effects)
			{
				if (!slot.TryReserve(effect.CopyBytes, out int offset))
				{
					return false;
				}

				int length = effect.Copy(slot.At(offset, effect.CopyBytes));
				slot.Effects[slot.EffectCount++] = (offset, Math.Max(length, 0));
			}
		}

		return true;
	}

	// worker: the effects take their coefficients from the slot's copies, in the same order
	internal void LoadEffects(PeriodSlot slot)
	{
		int at = 0;
		foreach (ShadowNode node in this.nodes)
		{
			foreach (BusEffect effect in node.Effects)
			{
				(int offset, int length) = slot.Effects[at++];
				effect.Load(slot.At(offset, length));
			}
		}
	}

	// the node a game object pointer belongs to, -1 for a destination the shadow does not model
	public int IndexOf(nint pointer)
	{
		for (int i = 0; i < this.nodes.Length; i++)
		{
			if (this.nodes[i].Pointer == pointer)
			{
				return i;
			}
		}

		return -1;
	}

	// MixIn: node += signal x gain
	public void Add(int node, ReadOnlySpan<float> signal, float gain)
	{
		Simd.MulAdd(this.nodes[node].Buffer.AsSpan(0, this.Frames * this.Channels), signal, gain);
		this.nodes[node].Inputs++;
	}

	// one period: submix processes in send order (sub_141EB72E0), master processes, Master ambisonics into Master bed, gain, fold to
	// stereo (output: Frames x 2). Clears the accumulators.
	public void Process(float gain, Span<float> output)
	{
		int length = this.Frames * this.Channels;
		foreach (int index in this.order)
		{
			ShadowNode node = this.nodes[index];
			Span<float> buffer = node.Buffer.AsSpan(0, length);
			if (node.Master)
			{
				MasterPath.ApplyMaster(buffer, this.Frames, this.Channels, node.Effects, node.Inputs, gain);
				continue;
			}

			// a submix sends when it had input or an effect ran (a tail)
			bool send = node.Inputs > 0;
			foreach (BusEffect effect in node.Effects)
			{
				if (effect.Ready && !effect.Skip && (node.Inputs > 0 || effect.Tail))
				{
					effect.Process(buffer, this.Frames, this.Channels);
					send = true;
				}
			}

			if (send)
			{
				for (int s = 0; s < node.SendTo.Length; s++)
				{
					this.Add(node.SendTo[s], buffer, node.SendGain[s]);
				}
			}
		}

		if (this.bed >= 0 && this.ambisonics >= 0)
		{
			MasterPath.AddAmbisonics(this.nodes[this.bed].Buffer, this.nodes[this.ambisonics].Buffer, length);
		}

		if (this.bed < 0)
		{
			output[..(this.Frames * 2)].Clear();
		}
		else if (this.Channels == 8)
		{
			MasterPath.FoldTo2(this.nodes[this.bed].Buffer, output, this.Frames);
		}
		else if (this.Channels == 2)
		{
			this.nodes[this.bed].Buffer.AsSpan(0, length).CopyTo(output);
		}
		else
		{
			output[..(this.Frames * 2)].Clear();
		}

		this.Clear();
	}

	// drops whatever the voices mixed in (a failed period)
	public void Clear()
	{
		foreach (ShadowNode node in this.nodes)
		{
			node.Buffer.AsSpan(0, this.Frames * this.Channels).Clear();
			node.Inputs = 0;
		}
	}
}
