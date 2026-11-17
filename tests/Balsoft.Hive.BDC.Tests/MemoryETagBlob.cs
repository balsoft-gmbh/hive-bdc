using System.Text;
using Balsoft.Hive.BDC.Storage;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>A blob in memory with ETags, a switch for an unreachable service and a hook for another writer.</summary>
internal sealed class MemoryETagBlob : IETagBlob
{
    private byte[]? _content;
    private int _version;

    public string Where => "https://account.blob.core.windows.net/settings/hivebdc.json";

    public bool Down { get; set; }

    public int Writes { get; private set; }

    public int Reads { get; private set; }

    /// <summary>Runs right before a conditional write, as another writer would between a read and a write.</summary>
    public Action<MemoryETagBlob>? BeforeWrite { get; set; }

    public string? Text => _content is null ? null : Encoding.UTF8.GetString(_content);

    public void Put(string json)
    {
        _content = Encoding.UTF8.GetBytes(json);
        _version++;
    }

    public (byte[] Content, string ETag)? Read()
    {
        if (Down) throw new InvalidOperationException("blob 503");
        Reads++;
        return _content is null ? null : (_content, "v" + _version);
    }

    public string? CurrentETag()
    {
        if (Down) throw new InvalidOperationException("blob 503");
        return _content is null ? null : "v" + _version;
    }

    public bool TryWrite(byte[] content, string? ifMatch)
    {
        var hook = BeforeWrite;
        BeforeWrite = null;
        hook?.Invoke(this);
        string? now = _content is null ? null : "v" + _version;
        if (now != ifMatch) return false;
        _content = content;
        _version++;
        Writes++;
        return true;
    }
}