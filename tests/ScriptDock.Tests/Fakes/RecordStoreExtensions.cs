using System.IO;
using System.Threading.Tasks;
using ScriptDock.Models;
using ScriptDock.Storage;

namespace ScriptDock.Tests.Fakes;

/// <summary>Test shorthands over the streamed record calls.</summary>
internal static class RecordStoreExtensions
{
    public static Task AddRunOutputAsync(this IRecordStore records, RunRecord run, byte[] output) =>
        AddAsync(records, run, output);

    private static async Task AddAsync(IRecordStore records, RunRecord run, byte[] output)
    {
        await using var stream = new MemoryStream(output, writable: false);
        await records.AddRunOutputAsync(run, stream, output.Length);
    }

    public static async Task<byte[]?> ReadRunOutputAsync(this IRecordReader reader, long id)
    {
        byte[]? bytes = null;
        var found = await reader.ReadRunOutputAsync(id, stream =>
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            bytes = copy.ToArray();
        });
        return found ? bytes : null;
    }
}
