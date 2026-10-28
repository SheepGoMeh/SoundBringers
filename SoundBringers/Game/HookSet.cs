using System;
using System.Collections.Generic;
using System.Threading;

using Dalamud.Hooking;

namespace SoundBringers.Game;

// Detours bracket their body (original call included) with Enter/Exit; Dispose drains them before freeing
internal sealed class HookSet: IDisposable
{
	private readonly List<IDalamudHook> hooks = [];
	private readonly List<Action> disablers = [];
	private int inFlight;

	// true when Dispose gave up and left hooks allocated
	public bool Leaked { get; private set; }

	public Hook<T> Add<T>(nint address, T detour) where T: Delegate
	{
		Hook<T> hook = Service.GameInteropProvider.HookFromAddress(address, detour);
		hook.Enable();
		this.hooks.Add(hook);
		this.disablers.Add(hook.Disable);
		return hook;
	}

	public void Enter() => Interlocked.Increment(ref this.inFlight);

	public void Exit() => Interlocked.Decrement(ref this.inFlight);

	public void Dispose()
	{
		foreach (Action disable in this.disablers)
		{
			disable();
		}

		long deadline = Environment.TickCount64 + 3000;
		while (Volatile.Read(ref this.inFlight) > 0 && Environment.TickCount64 < deadline)
		{
			Thread.Sleep(1);
		}

		// a thread past the patched jump but not yet counted
		Thread.Sleep(100);
		if (Volatile.Read(ref this.inFlight) > 0)
		{
			// leak: a thread may still run the trampoline
			this.Leaked = true;
			Service.PluginLog.Warning($"{this.inFlight} thread(s) still inside a detour after 3 s, hooks left allocated");
			return;
		}

		foreach (IDalamudHook hook in this.hooks)
		{
			hook.Dispose();
		}

		this.hooks.Clear();
	}
}
