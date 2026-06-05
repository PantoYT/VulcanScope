namespace VulcanScope;

/// <summary>Raised when the Hebe API returns a non-zero Status.Code (e.g. EDUVULCAN_PREMIUM).</summary>
public sealed class HebeException : Exception
{
    public int Code { get; }
    public string Url { get; }

    public HebeException(int code, string? message, string url)
        : base($"Hebe API error {code}: {message}  ({url})")
    {
        Code = code;
        Url = url;
    }
}
