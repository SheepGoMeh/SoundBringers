using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

using Dalamud.Hooking;

using SoundBringers.Dsp;
using SoundBringers.Game;

namespace SoundBringers.Mix;

internal delegate void ShadowRenderFn(nint a1);

internal delegate nint ShadowVoiceFn(nint voice, nint scratch);

// The stream's own mix. The audio thread only copies: the voice detour copies each voice's state and queued audio around the game's
// render, the render detour the effects and volume inputs before the period, then hands the slot to the worker. The worker does all
// the DSP and writes the ring. With no free slot the period is dropped for the stream.
//
// Threads: the graph is built on the framework thread and published by one reference swap; the audio thread reads it for the copy, the
// worker owns it while processing (the slot carries its graph). Nothing on the audio thread allocates. In inline mode the audio thread
// does the worker's part itself, right after publishing the slot.
internal sealed class ShadowMixer: IDisposable
{
	private const int SampleRate = 48000;
	private const int MaxFrames = 4096;
	private const int MaxSends = 16;
	private const int MaxEffects = 8;
	private const int SlotCount = 4;
	private const int SlotArenaBytes = 4 * 1024 * 1024;
	private const int SlotVoices = 512;
	private const int SlotEffects = 160; // 17 nodes x up to 8 effects, with headroom
	private const int MasterEqualizerVtableRva = 0x254D180; // the user equalizer's core effect on the masters, build 2026.10.20
	private const int MasterBed = 2;
	private const int MasterAmbisonics = 4;
	private const long LogGapMs = 10000;
	private const int JoinMs = 2000;

	private readonly StreamLevels levels;
	private readonly SampleRing ring;
	private readonly HookSet hooks = new();
	private readonly float[] silence = new float[MaxFrames * 2];
	private readonly nint moduleBase = Service.SigScanner.Module.BaseAddress;
	private static readonly MemoryRead Reader = GameAudio.TryRead;

	private readonly SlotQueue queue = new(SlotCount, SlotArenaBytes, SlotVoices, SlotEffects);
	private readonly AutoResetEvent signal = new(false);
	private readonly PeriodCopier copier = new(GameAudio.TryRead);
	private readonly PeriodProcessor processor = new();
	private Thread? worker;
	private volatile bool stopping;
	private volatile bool inline;

	// who processes the queue: the worker, or the audio thread in inline mode; never both at once
	private int draining;

	// worker only: dropped periods already written as silence
	private long gapsHandled;

	// framework thread only
	private readonly Dictionary<(nint Pointer, string Kind), BusEffect> effects = [];
	private readonly HashSet<nint> unknownEqualizer = [];
	private List<long> signature = [];
	private string lastRefreshError = string.Empty;

	// audio thread: the slot being filled (null outside a period or when it is dropped), its thread id, and the dropped periods so far
	private PeriodSlot? current;
	private int audioThread;
	private long gaps;
	private int gapFrames;

	// shared: the worker asks the next refresh for fresh effects
	private int rebuild;
	private long lastLogMs;

	private ShadowGraph? graph;
	private volatile bool enabled = true;
	private Hook<ShadowRenderFn>? renderHook;
	private Hook<ShadowVoiceFn>? voiceHook;

	public ShadowMixer(StreamLevels levels, SampleRing ring)
	{
		this.levels = levels;
		this.ring = ring;
		this.processor.Fault = this.Fault;
		try
		{
			this.Refresh();
			Prewarm();
			this.StartWorker();
			Volatile.Write(ref this.voiceHook, this.hooks.Add<ShadowVoiceFn>(GameAudio.VoiceFunction, this.OnVoice));
			Volatile.Write(ref this.renderHook, this.hooks.Add<ShadowRenderFn>(GameAudio.RenderFunction, this.OnRender));
		}
		catch
		{
			this.hooks.Dispose();
			if (this.StopWorker() && !this.hooks.Leaked)
			{
				this.signal.Dispose();
			}

			throw;
		}
	}

	// JIT-compiles what the detours and the worker run, so no period waits for the compiler or a type initializer
	private static void Prewarm()
	{
		Type[] types =
		[
			typeof(VoiceRenderer), typeof(VoiceFilter), typeof(VoiceStateTable), typeof(ShadowGraph), typeof(ShadowNode), typeof(BusEffect),
			typeof(MasterPath), typeof(Biquad), typeof(LowpassEffect), typeof(WahWahEffect), typeof(FaithReverbEffect),
			typeof(StringendoEffect), typeof(EqualizerEffect), typeof(MasterEqualizerEffect), typeof(VoiceVolume), typeof(VolumeCache), typeof(PeriodSlot), typeof(SlotQueue), typeof(VoiceRecord), typeof(PeriodCopier), typeof(PeriodProcessor),
			typeof(VolumeChain), typeof(SoundTerms), typeof(RefillInference), typeof(Categories), typeof(StreamLevels.Snapshot),
			typeof(GameAudio), typeof(ShadowMixer), typeof(SampleRing), typeof(HookSet), typeof(StreamLevels),
			typeof(VoiceInput), typeof(EffectLayout), typeof(VolumeFollow), typeof(FaderTracker),
			typeof(VoiceStateTable.History), typeof(Hook<ShadowRenderFn>), typeof(Hook<ShadowVoiceFn>),
		];
		const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
		foreach (Type type in types)
		{
			List<Type> all = [type, .. type.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)];
			foreach (Type t in all)
			{
				List<MethodBase> methods = [.. t.GetMethods(All), .. t.GetConstructors(All)];
				foreach (MethodBase method in methods)
				{
					if (method.IsAbstract || method.ContainsGenericParameters || (method is MethodInfo info && info.IsGenericMethod))
					{
						continue;
					}

					try
					{
						RuntimeHelpers.PrepareMethod(method.MethodHandle);
					}
					catch (Exception)
					{
						// compiled on first call instead
					}
				}
			}
		}

		// the generic Await instantiations the detours use
		MethodInfo await = typeof(ShadowMixer).GetMethod(nameof(Await), All)!;
		foreach (Type delegateType in new[] { typeof(ShadowRenderFn), typeof(ShadowVoiceFn) })
		{
			try
			{
				RuntimeHelpers.PrepareMethod(await.MakeGenericMethod(delegateType).MethodHandle);
			}
			catch (Exception)
			{
				// compiled on first call instead
			}
		}

		// and one period through a throwaway graph
		ShadowNode submix = new(1, false, [1], [1f], [new BusEffect(1, true, (b, f, c) => { })], 4 * 8);
		ShadowGraph graph = new([submix, new ShadowNode(2, true, [], [], [], 4 * 8)], 1, -1, 4, 8);
		graph.Add(0, graph.Scratch, 1f);
		graph.Process(1f, graph.Output);
	}

	// the stream's on switch; streaming also needs a graph
	public bool Enabled
	{
		get => this.enabled;
		set => this.enabled = value;
	}

	public bool HasGraph => Volatile.Read(ref this.graph) != null;

	// processes each period on the game's audio thread, at the end of the render detour, instead of on the worker: no handoff, but
	// the game's audio period waits for the stream's DSP
	public bool Inline
	{
		get => this.inline;
		set => this.inline = value;
	}

	// the detours stop first, then the worker; the signal is freed only when no detour can still set it
	public void Dispose()
	{
		this.hooks.Dispose();
		bool stopped = this.StopWorker();
		Volatile.Write(ref this.graph, null);
		if (stopped && !this.hooks.Leaked)
		{
			this.signal.Dispose();
		}
	}

	// ---- worker thread ------------------------------------------------------------------------------------------------------

	private void StartWorker()
	{
		this.worker = new Thread(this.Work)
		{
			Name = "SoundBringers mixer",
			IsBackground = true,
			Priority = ThreadPriority.AboveNormal,
		};

		this.worker.Start();
	}

	// false when the worker did not end in time
	private bool StopWorker()
	{
		Thread? thread = this.worker;
		if (thread == null)
		{
			return true;
		}

		this.stopping = true;
		try
		{
			this.signal.Set();
		}
		catch (ObjectDisposedException)
		{
			// already freed
		}

		bool joined = thread.Join(JoinMs);
		if (!joined)
		{
			Service.PluginLog.Warning("The mixer worker did not stop in time");
		}

		return joined;
	}

	// the wait times out only so a missed stop signal is still noticed
	private void Work()
	{
		while (!this.stopping)
		{
			this.signal.WaitOne(50);
			if (!this.inline)
			{
				this.TryDrain();
			}
		}
	}

	// worker, or the audio thread in inline mode: every published slot. Skipped when the other side is draining (a mode switch);
	// what it leaves is drained next time.
	private void TryDrain()
	{
		if (Interlocked.CompareExchange(ref this.draining, 1, 0) != 0)
		{
			return;
		}

		try
		{
			while (true)
			{
				// the gap count is read before the queue, so a drop that came after a slot never overtakes it
				long gapsNow = Volatile.Read(ref this.gaps);
				PeriodSlot? slot = this.queue.Peek();
				if (slot == null)
				{
					this.CatchUp(gapsNow);
					break;
				}

				this.CatchUp(slot.GapsBefore);
				try
				{
					this.Handle(slot);
				}
				catch (Exception e)
				{
					this.Fault(e);
					try
					{
						slot.Graph?.Clear();
						this.WriteSilence(slot.Frames);
					}
					catch (Exception)
					{
						// nothing more to do for this period
					}
				}

				this.queue.Release();
			}
		}
		finally
		{
			Volatile.Write(ref this.draining, 0);
		}
	}

	// each dropped period is silence in the ring, so the stream stays in step, and a gap in the voice history
	private void CatchUp(long target)
	{
		while (this.gapsHandled < target)
		{
			this.gapsHandled++;
			this.processor.Gap();
			this.WriteSilence(Volatile.Read(ref this.gapFrames));
		}
	}

	private void Handle(PeriodSlot slot)
	{
		PeriodProcessor.Outcome outcome = this.processor.Process(slot);
		ShadowGraph? graph = slot.Graph;
		if (graph == null)
		{
			this.WriteSilence(slot.Frames);
			return;
		}

		if (outcome == PeriodProcessor.Outcome.NonFinite)
		{
			Volatile.Write(ref this.rebuild, 1);
			this.WriteSilence(graph.Frames);
			return;
		}

		this.ring.Write(graph.Output);
	}

	private void WriteSilence(int frames)
	{
		if (frames is >= 1 and <= MaxFrames)
		{
			this.ring.Write(this.silence.AsSpan(0, frames * 2));
		}
	}

	// ---- framework thread ---------------------------------------------------------------------------------------------------

	// publishes a new shadow graph when the game's differs; about once per second
	public void Refresh()
	{
		try
		{
			if (!GameAudio.Ready)
			{
				Volatile.Write(ref this.graph, null);
				return;
			}

			int frames = GameAudio.MixerFrames();
			int channels = GameAudio.MixerChannels();
			if (frames is < 1 or > MaxFrames || channels is not (2 or 8) || GameAudio.MixerRate() != SampleRate)
			{
				Volatile.Write(ref this.graph, null);
				this.signature = [];
				this.RefreshError($"Unsupported mixer format ({frames} frames, {channels} channels, {GameAudio.MixerRate()} Hz)");
				return;
			}

			if (Interlocked.Exchange(ref this.rebuild, 0) == 1)
			{
				this.effects.Clear();
				this.signature = [];
			}

			List<Description> nodes = Describe();
			List<long> now = Signature(frames, channels, nodes);
			if (Volatile.Read(ref this.graph) != null && CollectionsMarshal.AsSpan(now).SequenceEqual(CollectionsMarshal.AsSpan(this.signature)))
			{
				return;
			}

			ShadowGraph built = this.Build(nodes, frames, channels);
			this.signature = now;
			Volatile.Write(ref this.graph, built);
			this.lastRefreshError = string.Empty;
		}
		catch (Exception e)
		{
			Volatile.Write(ref this.graph, null);
			this.signature = [];
			this.RefreshError(e.Message);
		}
	}

	private void RefreshError(string message)
	{
		if (message != this.lastRefreshError)
		{
			this.lastRefreshError = message;
			Service.PluginLog.Warning($"Shadow mixer has no graph: {message}");
		}
	}

	private sealed record Description(nint Pointer, bool Master, int Index, (nint Destination, float Gain)[] Sends, nint[] Effects);

	// submixes 0..14 that exist, then Master bed and Master ambisonics
	private static List<Description> Describe()
	{
		List<Description> list = [];
		for (int i = 0; i < GameAudio.SubmixCount; i++)
		{
			nint submix = GameAudio.Submix(i);
			if (submix != 0)
			{
				list.Add(Read(submix, false, i, GameAudio.SubmixSends, GameAudio.SubmixSendCount, GameAudio.SubmixEffects, GameAudio.SubmixEffectCount));
			}
		}

		for (int i = 0; i < GameAudio.MasterCount; i++)
		{
			nint master = GameAudio.Master(i);
			int bus = master != 0 ? GameAudio.Rd<int>(master + GameAudio.MasterBusId) : 0;
			if (bus is MasterBed or MasterAmbisonics)
			{
				list.Add(Read(master, true, bus, 0, 0, GameAudio.MasterEffects, GameAudio.MasterEffectCount));
			}
		}

		return list;
	}

	private static Description Read(nint voice, bool master, int index, int sendsAt, int sendCountAt, int effectsAt, int effectCountAt)
	{
		int sendCount = sendCountAt != 0 ? Math.Clamp(GameAudio.Rd<int>(voice + sendCountAt), 0, MaxSends) : 0;
		(nint, float)[] sends = new (nint, float)[sendCount];
		for (int s = 0; s < sendCount; s++)
		{
			nint send = voice + sendsAt + (s * GameAudio.SendSize);
			sends[s] = (GameAudio.Rd<nint>(send + GameAudio.SendDestination), GameAudio.Rd<float>(send + GameAudio.SendGain));
		}

		nint[] list = new nint[Math.Clamp(GameAudio.Rd<int>(voice + effectCountAt), 0, MaxEffects)];
		for (int e = 0; e < list.Length; e++)
		{
			list[e] = GameAudio.Rd<nint>(voice + effectsAt + (8 * e));
		}

		return new Description(voice, master, index, sends, list);
	}

	private static List<long> Signature(int frames, int channels, List<Description> nodes)
	{
		List<long> list = [frames, channels];
		foreach (Description node in nodes)
		{
			list.Add(node.Pointer);
			list.Add(node.Index);
			list.Add(node.Sends.Length);
			foreach ((nint destination, float gain) in node.Sends)
			{
				list.Add(destination);
				list.Add(BitConverter.SingleToInt32Bits(gain));
			}

			list.Add(node.Effects.Length);
			foreach (nint effect in node.Effects)
			{
				list.Add(effect);
			}
		}

		return list;
	}

	private ShadowGraph Build(List<Description> nodes, int frames, int channels)
	{
		Dictionary<(nint, string), BusEffect> used = [];
		ShadowNode[] built = new ShadowNode[nodes.Count];
		int bed = -1;
		int ambisonics = -1;
		for (int i = 0; i < nodes.Count; i++)
		{
			Description node = nodes[i];
			List<int> to = [];
			List<float> gains = [];
			foreach ((nint destination, float gain) in node.Sends)
			{
				int at = nodes.FindIndex(n => n.Pointer == destination);
				if (at >= 0)
				{
					to.Add(at);
					gains.Add(gain);
				}
			}

			List<BusEffect> list = [];
			for (int e = 0; e < node.Effects.Length; e++)
			{
				BusEffect? effect = this.Effect(node.Master, node.Index, e, node.Effects[e], used);
				if (effect != null)
				{
					list.Add(effect);
				}
			}

			built[i] = new ShadowNode(node.Pointer, node.Master, [.. to], [.. gains], [.. list], frames * channels);
			if (node.Master)
			{
				if (node.Index == MasterBed)
				{
					bed = i;
				}
				else
				{
					ambisonics = i;
				}
			}
		}

		ShadowGraph graph = new(built, bed, ambisonics, frames, channels);

		// effects no longer in the graph are dropped with their delay lines
		this.effects.Clear();
		foreach (KeyValuePair<(nint, string), BusEffect> pair in used)
		{
			this.effects[pair.Key] = pair.Value;
		}

		return graph;
	}

	// which effect class sits where; anything else (the analyzers) passes the audio through
	private static string? KindOf(bool master, int index, int slot) => (master, index, slot) switch
	{
		(true, _, 0) => "lowpass",
		(true, _, 1) => "mastereq",
		(false, 1, 0) => "reverb",
		(false, 3, 0) => "wahwah",
		(false, 3, 1) => "lowpass",
		(false, 4 or 14, 0) => "lowpass",
		(false, 10, 0) => "stringendo",
		(false, 11 or 12 or 13, 0) => "equalizer",
		_ => null,
	};

	private BusEffect? Effect(bool master, int index, int slot, nint pointer, Dictionary<(nint, string), BusEffect> used)
	{
		string? kind = KindOf(master, index, slot);
		if (kind == null || pointer == 0)
		{
			return null;
		}

		(nint, string) key = (pointer, kind);
		if (!this.effects.TryGetValue(key, out BusEffect? effect))
		{
			nint core = pointer - PeriodCopier.EffectBack;
			switch (kind)
			{
				case "lowpass":
					LowpassEffect lowpass = new();
					effect = new BusEffect(pointer, false, lowpass.Process, LowpassEffect.Size, c => LowpassEffect.Copy(Reader, core, c), lowpass.LoadCopy);
					break;
				case "wahwah":
					WahWahEffect wahwah = new();
					effect = new BusEffect(pointer, false, wahwah.Process, WahWahEffect.Size, c => WahWahEffect.Copy(Reader, core, c), wahwah.LoadCopy);
					break;
				case "reverb":
					FaithReverbEffect reverb = new();
					effect = new BusEffect(pointer, true, reverb.Process, FaithReverbEffect.CopyBytes, c => FaithReverbEffect.Copy(Reader, core, c), reverb.LoadCopy);
					break;
				case "stringendo":
					StringendoEffect stringendo = new();
					effect = new BusEffect(pointer, false, stringendo.Process, StringendoEffect.CopyBytes, c => StringendoEffect.Copy(Reader, core, c), stringendo.LoadCopy);
					break;
				case "equalizer":
					EqualizerEffect equalizer = new();
					effect = new BusEffect(pointer, false, equalizer.Process, EqualizerEffect.CopyBytes, c => EqualizerEffect.Copy(Reader, core, c), equalizer.LoadCopy);
					break;
				default:
					// the user equalizer: only the known class has its stages and gain where the port reads them
					if (GameAudio.Rd<nint>(core) != this.moduleBase + MasterEqualizerVtableRva)
					{
						if (this.unknownEqualizer.Add(pointer))
						{
							Service.PluginLog.Warning($"Master effect {pointer:X} is not the known user equalizer class: left out of the stream");
						}

						return null;
					}

					MasterEqualizerEffect userEqualizer = new();
					effect = new BusEffect(pointer, false, userEqualizer.Process, MasterEqualizerEffect.Size, c => MasterEqualizerEffect.Copy(Reader, core, c), userEqualizer.LoadCopy);
					break;
			}
		}

		used[key] = effect;
		return effect;
	}

	// ---- audio thread -------------------------------------------------------------------------------------------------------

	private static Hook<T> Await<T>(ref Hook<T>? field) where T: Delegate
	{
		SpinWait wait = default;
		Hook<T>? hook;
		while ((hook = Volatile.Read(ref field)) == null)
		{
			wait.SpinOnce();
		}

		return hook;
	}

	private void OnRender(nint a1)
	{
		this.hooks.Enter();
		try
		{
			PeriodSlot? slot;
			try
			{
				slot = this.BeginPeriod();
			}
			catch (Exception e)
			{
				this.Fault(e);
				slot = null;
			}

			try
			{
				Await(ref this.renderHook).Original(a1);
			}
			finally
			{
				this.current = null;
				this.EndPeriod(slot);
			}
		}
		finally
		{
			this.hooks.Exit();
		}
	}

	// null when the period is dropped; a slot with no graph is a silent period
	private PeriodSlot? BeginPeriod()
	{
		this.current = null;
		PeriodSlot? slot = this.queue.TryBegin();
		if (slot == null)
		{
			return this.Drop();
		}

		try
		{
			if (!this.Fill(slot))
			{
				return this.Drop();
			}
		}
		catch (Exception e)
		{
			this.Fault(e);
			slot.Graph = null;
			this.current = null;
		}

		return slot;
	}

	// false when the slot has no room
	private bool Fill(PeriodSlot slot)
	{
		ShadowGraph? graph = Volatile.Read(ref this.graph);
		StreamLevels.Snapshot snapshot = this.levels.Current;
		slot.Frames = graph?.Frames ?? GameAudio.MixerFrames();
		if (graph == null || !this.enabled || !GameAudio.Ready || GameAudio.SpatialActive() || GameAudio.MixSkipped() ||
		    (snapshot.MuteInBackground && this.levels.Background) ||
		    GameAudio.MixerFrames() != graph.Frames || GameAudio.MixerChannels() != graph.Channels)
		{
			return true;
		}

		int rate = GameAudio.MixerRate();
		if (rate != SampleRate)
		{
			return true;
		}

		if (!graph.CopyEffects(slot))
		{
			return false;
		}

		slot.Rate = rate;
		slot.Levels = snapshot;
		nint soundManager = GameAudio.SoundManager();
		if (!slot.TryReserve(PeriodSlot.ManagerBytes, out int managerAt))
		{
			return false;
		}

		slot.ManagerOffset = managerAt;
		slot.HasManager = soundManager != 0 && GameAudio.TryRead(soundManager, slot.At(managerAt, PeriodSlot.ManagerBytes));
		slot.BusCount = GameAudio.BusPointers(slot.BusPointers);
		slot.HasGlobals = GameAudio.CategoryGlobals;
		if (slot.HasGlobals)
		{
			slot.Mode = GameAudio.Rd<int>(GameAudio.CategoryMode);
			slot.Player = GameAudio.Rd<float>(GameAudio.CategoryPlayer);
			slot.Party = GameAudio.Rd<float>(GameAudio.CategoryParty);
			slot.Other = GameAudio.Rd<float>(GameAudio.CategoryOther);
		}

		this.audioThread = Environment.CurrentManagedThreadId;
		slot.Graph = graph;
		this.current = slot;
		return true;
	}

	private PeriodSlot? Drop()
	{
		// the worker writes the silence; it only needs the count and the length
		Volatile.Write(ref this.gapFrames, GameAudio.MixerFrames());
		Volatile.Write(ref this.gaps, this.gaps + 1);
		try
		{
			this.signal.Set();
		}
		catch (Exception)
		{
			// disposing
		}

		this.current = null;
		return null;
	}

	private void EndPeriod(PeriodSlot? slot)
	{
		if (slot == null)
		{
			return;
		}

		slot.GapsBefore = this.gaps;
		this.queue.Publish();
		if (this.inline)
		{
			try
			{
				this.TryDrain();
			}
			catch (Exception e)
			{
				this.Fault(e);
			}

			return;
		}

		try
		{
			this.signal.Set();
		}
		catch (Exception)
		{
			// disposing
		}
	}

	private void Fault(Exception e)
	{
		try
		{
			long now = Environment.TickCount64;
			if (now - this.lastLogMs >= LogGapMs)
			{
				this.lastLogMs = now;
				Service.PluginLog.Error(e, "Shadow mixer failed");
			}
		}
		catch (Exception)
		{
			// the fault handler must not fault
		}
	}

	private nint OnVoice(nint voice, nint scratch)
	{
		this.hooks.Enter();
		try
		{
			PeriodSlot? slot = this.current;
			bool shadow = false;
			if (slot != null && Environment.CurrentManagedThreadId == this.audioThread)
			{
				try
				{
					shadow = this.copier.BeginVoice(slot, voice);
				}
				catch (Exception e)
				{
					this.Fault(e);
					this.copier.Abandon(slot);
				}
			}

			nint result = Await(ref this.voiceHook).Original(voice, scratch);
			if (shadow)
			{
				try
				{
					this.copier.EndVoice(slot!, voice);
				}
				catch (Exception e)
				{
					this.Fault(e);
					this.copier.Abandon(slot!);
				}
			}

			return result;
		}
		finally
		{
			this.hooks.Exit();
		}
	}
}
