using System.Buffers;
using System.Runtime.CompilerServices;

internal sealed class NatsSubjectMatcher
{
    private sealed class Node
    {
        // Literal children (token -> node)
        public Dictionary<string, Node>? Literals;
        // Wildcard child for '*'
        public Node? Star;
        // If a '>' subscription ends here, store its pattern id (the most recently added wins ties)
        public int GtPattern = -1;
        // If an exact-end subscription ends here, store its pattern id
        public int EndPattern = -1;
    }

    private readonly Node _root = new Node();
    private readonly string[] _patterns; // id -> original pattern (for return)
    private readonly StringComparer _cmp = StringComparer.Ordinal; // NATS subjects are case-sensitive

    public NatsSubjectMatcher(IEnumerable<string> patterns)
    {
        // materialize to array to keep stable ids
        var list = new List<string>();
        foreach (var p in patterns)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            list.Add(p);
            InsertPattern(p, id: list.Count - 1);
        }
        _patterns = list.ToArray();
    }

    public string? Match(ReadOnlySpan<char> subject)
    {
        // Tokenize subject into spans (no string allocations)
        Span<Range> ranges = stackalloc Range[32];
        int tokCount = SplitTokens(subject, ref ranges);
        if (tokCount == -1)
        {
            // Fallback for unusually long subjects
            var rent = ArrayPool<Range>.Shared.Rent(128);
            try
            {
                var span = rent.AsSpan();
                tokCount = SplitTokens(subject, ref span);
                return MatchTokens(subject, span, tokCount);
            }
            finally
            {
                ArrayPool<Range>.Shared.Return(rent);
            }
        }
        return MatchTokens(subject, ranges, tokCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string? MatchTokens(ReadOnlySpan<char> subject, ReadOnlySpan<Range> tokens, int count)
    {
        // Greedy walk with backtracking stack to respect exact > * > > specificity
        var stack = new ValueStack(32);
        var (node, index) = (_root, 0);

        // Best match found so far (by depth)
        int bestPattern = -1;
        int bestDepth = -1;

        while (true)
        {
            // If '>' is present at this node, it matches the remaining tail at any depth.
            if (node.GtPattern >= 0)
            {
                // depth counts as full length to beat shorter matches
                if (count >= bestDepth)
                {
                    bestDepth = count;
                    bestPattern = node.GtPattern;
                }
            }

            // If we’ve consumed all tokens, check EndPattern
            if (index == count)
            {
                if (node.EndPattern >= 0 && index >= bestDepth)
                {
                    bestDepth = index;
                    bestPattern = node.EndPattern;
                }
                // Exhausted current path; backtrack if possible
                if (!stack.Pop(ref node, ref index)) break;
                continue;
            }

            // Current token span
            var tok = tokens[index];
            var token = subject[tok];

            // Try literal first
            Node? child = null;
            if (node.Literals != null && node.Literals.Count != 0)
            {
                // We avoid allocating strings for tokens by probing each literal key length first.
                // Fast path: exact key lookup via string slice copy only when key length matches.
                // To keep it simple and fast, we precompute a small string once when needed.
                if (node.Literals.TryGetValue(token.ToString(), out child))
                {
                    // Push alternative paths for backtracking (* and then '>' if present)
                    if (node.Star != null)
                        stack.Push(node.Star, index + 1);
                    // Descend literal
                    node = child;
                    index++;
                    continue;
                }
            }

            // Try '*'
            if (node.Star != null)
            {
                // Push nothing extra here; we’ll continue down '*'
                node = node.Star;
                index++;
                continue;
            }

            // No way forward; backtrack
            if (!stack.Pop(ref node, ref index)) break;
        }

        return bestPattern >= 0 ? _patterns[bestPattern] : null;
    }

    private void InsertPattern(string pattern, int id)
    {
        var node = _root;
        var span = pattern.AsSpan();

        // Quick parse of tokens separated by '.'
        int start = 0;
        for (int i = 0; i <= span.Length; i++)
        {
            if (i == span.Length || span[i] == '.')
            {
                var part = span.Slice(start, i - start);
                if (part.Length == 0)
                {
                    // Skip empty token (defensive; NATS typically has non-empty tokens)
                    start = i + 1;
                    continue;
                }

                if (part.Length == 1 && part[0] == '>')
                {
                    // '>' must be last; mark tail-match subscription at this node
                    node.GtPattern = id;
                    // Ignore anything after '>' if present
                    node.EndPattern = -1; // '>' overrides exact if both present (tail wins for depth)
                    return;
                }
                else if (part.Length == 1 && part[0] == '*')
                {
                    node.Star ??= new Node();
                    node = node.Star;
                }
                else
                {
                    node.Literals ??= new Dictionary<string, Node>(_cmp);
                    var key = part.ToString(); // once per pattern build (not per match)
                    if (!node.Literals.TryGetValue(key, out var child))
                    {
                        child = new Node();
                        node.Literals[key] = child;
                    }
                    node = child;
                }

                start = i + 1;
            }
        }

        // Exact end of pattern
        node.EndPattern = id;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SplitTokens(ReadOnlySpan<char> s, ref Span<Range> outRanges)
    {
        int count = 0;
        int start = 0;
        for (int i = 0; i <= s.Length; i++)
        {
            if (i == s.Length || s[i] == '.')
            {
                if (count >= outRanges.Length)
                    return -1; // signal: need larger buffer
                outRanges[count++] = new Range(start, i);
                start = i + 1;
            }
        }
        return count;
    }

    // Tiny manual stack to avoid recursion and large allocations
    private struct ValueStack
    {
        private Node?[] _nodes;
        private int[] _idx;
        private int _top;

        public ValueStack(int capacity)
        {
            _nodes = new Node?[capacity];
            _idx = new int[capacity];
            _top = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(Node node, int index)
        {
            if (_top == _nodes.Length)
            {
                Array.Resize(ref _nodes, _nodes.Length * 2);
                Array.Resize(ref _idx, _idx.Length * 2);
            }
            _nodes[_top] = node;
            _idx[_top] = index;
            _top++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Pop(ref Node node, ref int index)
        {
            if (_top == 0) return false;
            _top--;
            node = _nodes[_top]!;
            index = _idx[_top];
            return true;
        }
    }
}