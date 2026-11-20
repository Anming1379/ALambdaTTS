namespace ALambdaTTS.Services.EdgeTts;

/// <summary>合成过程中的可预期错误（网络、服务端拒绝、没有音频）。</summary>
public sealed class EdgeTtsException : Exception
{
    public EdgeTtsException(string message)
        : base(message)
    {
    }

    public EdgeTtsException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
