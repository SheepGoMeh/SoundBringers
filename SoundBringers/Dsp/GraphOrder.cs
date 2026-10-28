using System;
using System.Collections.Generic;

namespace SoundBringers.Dsp;

public static class GraphOrder
{
	// topological order (Kahn): every node comes before the nodes it sends to; throws on a cycle
	public static int[] Order(IReadOnlyList<int[]> sendsByNode)
	{
		int count = sendsByNode.Count;
		int[] incoming = new int[count];
		foreach (int[] sends in sendsByNode)
		{
			foreach (int destination in sends)
			{
				incoming[destination]++;
			}
		}

		int[] order = new int[count];
		int tail = 0;
		for (int i = 0; i < count; i++)
		{
			if (incoming[i] == 0)
			{
				order[tail++] = i;
			}
		}

		for (int head = 0; head < tail; head++)
		{
			foreach (int destination in sendsByNode[order[head]])
			{
				if (--incoming[destination] == 0)
				{
					order[tail++] = destination;
				}
			}
		}

		return tail == count ? order : throw new InvalidOperationException("The send graph has a cycle");
	}
}
