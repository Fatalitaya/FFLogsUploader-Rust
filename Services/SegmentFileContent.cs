using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FFLogsPlugin.Services;

internal sealed class SegmentFileContent(string path, Action<long, long> progress) : HttpContent
{
    private readonly long length = new FileInfo(path).Length;

    protected override bool TryComputeLength(out long size)
    {
        size = length;
        return true;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
    {
        using var file = File.OpenRead(path);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            progress(0, length);
            long nextUpdate = Environment.TickCount64 + 200;
            int read;
            while ((read = await file.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                if (Environment.TickCount64 >= nextUpdate || file.Position == length)
                {
                    progress(file.Position, length);
                    nextUpdate = Environment.TickCount64 + 200;
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
