using System.Runtime.CompilerServices;

namespace Avalonia3DViewer.Core.Loading;

/// <summary>
/// Span 分词助手：按分隔符切分并跳过空 token（避免为超大文本文件逐行分配字符串数组）。
/// </summary>
internal static class Tokenizer
{
    /// <summary>枚举 span 中的非空 token。</summary>
    public static TokenEnumerator Tokens(ReadOnlySpan<char> span, char separator = ' ') => new(span, separator);

    internal ref struct TokenEnumerator
    {
        private ReadOnlySpan<char> _span;
        private readonly char _separator;
        private int _index;

        public TokenEnumerator(ReadOnlySpan<char> span, char separator)
        {
            _span = span;
            _separator = separator;
            _index = 0;
            Current = default;
        }

        public ReadOnlySpan<char> Current { get; private set; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            while (_index < _span.Length)
            {
                int start = _index;
                while (_index < _span.Length && _span[_index] != _separator) _index++;
                var token = _span[start.._index];
                _index++; // 跳过分隔符
                if (!token.IsEmpty)
                {
                    Current = token;
                    return true;
                }
            }
            Current = default;
            return false;
        }

        public TokenEnumerator GetEnumerator() => this;
    }
}
