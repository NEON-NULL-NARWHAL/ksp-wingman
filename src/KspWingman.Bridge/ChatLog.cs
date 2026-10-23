// KSP Wingman · src/KspWingman.Bridge/ChatLog.cs
// Numbers LMP's chat lines as they appear, so an agent can ask "what's new since #12" without seeing a line twice.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace KspWingman.Bridge
{
    /// <summary>One chat line with its number (1, 2, 3, ... in the order this game saw them).</summary>
    public sealed class ChatLine
    {
        public ChatLine(int id, string from, string text)
        {
            Id = id;
            From = from;
            Text = text;
        }

        public int Id { get; }
        public string From { get; }
        public string Text { get; }
    }

    /// <summary>
    /// LMP keeps the last few chat lines (ChatSystem.ChatMessages, a queue of (from, text, time) that drops the oldest).
    /// Each line is a new object, so a line this log hasn't seen yet is one it doesn't hold a reference to. Lines that
    /// scroll out of LMP's queue between two looks are missed; the agent looks every second or so.
    /// </summary>
    public sealed class ChatLog
    {
        /// <summary>At most this many numbered lines are kept; older ones are dropped.</summary>
        public const int Keep = 500;

        private readonly HashSet<object> _seen = new HashSet<object>(ReferenceComparer.Instance);
        private readonly List<ChatLine> _lines = new List<ChatLine>();
        private int _next = 1;

        /// <summary>Takes in what LMP holds now: (from, text) pairs, as objects whose identity marks each line.</summary>
        public void Update<T>(IEnumerable<T> current, Func<T, string> from, Func<T, string> text) where T : class
        {
            var now = new HashSet<object>(ReferenceComparer.Instance);
            foreach (var line in current)
            {
                now.Add(line);
                if (_seen.Contains(line))
                    continue;
                _lines.Add(new ChatLine(_next++, from(line) ?? "", text(line) ?? ""));
            }
            _seen.Clear();
            _seen.UnionWith(now); // only what LMP still holds, so this stays small
            if (_lines.Count > Keep)
                _lines.RemoveRange(0, _lines.Count - Keep);
        }

        /// <summary>The lines after number <paramref name="after"/> (0 for all that are kept).</summary>
        public List<ChatLine> After(int after) => _lines.FindAll(l => l.Id > after);

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public new bool Equals(object x, object y) => ReferenceEquals(x, y);

            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
