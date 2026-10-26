using System.Text;

namespace Avalonia3DViewer.Core.Loading;

/// <summary>
/// 支持 LF / CRLF / CR（经典 Mac）三种换行符的 ASCII 行读取器。
/// PLY 等格式的头部为文本行，真实世界文件中三种换行均可能出现
/// （例如 Rhinoceros 导出的二进制 PLY 使用纯 CR）。
/// </summary>
internal sealed class AsciiLineReader
{
    private readonly Stream _stream;
    private int _pushback = -1;

    public AsciiLineReader(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>读取一行（不含换行符）；流结束时返回 null（无内容时）。</summary>
    public string? ReadLine()
    {
        var sb = new StringBuilder(256);
        bool any = false;
        while (true)
        {
            int b = _pushback >= 0 ? _pushback : _stream.ReadByte();
            _pushback = -1;

            if (b < 0)
                return any ? sb.ToString() : null;
            any = true;

            if (b == '\n')
                return sb.ToString();

            if (b == '\r')
            {
                // CRLF：吃掉紧随的 LF；CR-only：将下一字节放回
                int next = _stream.ReadByte();
                if (next >= 0 && next != '\n')
                    _pushback = next;
                return sb.ToString();
            }

            sb.Append((char)b);
        }
    }

    /// <summary>
    /// 将因 CR-only 换行而窥视的字节归还流（读取头部结束后、切换为二进制读取前必须调用）。
    /// </summary>
    public void Rewind()
    {
        if (_pushback < 0) return;
        if (!_stream.CanSeek)
            throw new InvalidOperationException("流不可定位，无法归还窥视的字节。");
        _stream.Seek(-1, SeekOrigin.Current);
        _pushback = -1;
    }
}
