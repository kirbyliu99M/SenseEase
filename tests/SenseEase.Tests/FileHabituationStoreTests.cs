using System;
using System.IO;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Alpha-1 W2 (H16 / NE15): FileHabituationStore.Save now routes through the
/// crash-safe AtomicJsonWriter. Round-trip + overwrite must preserve the earned
/// offset, and the on-disk JSON shape must stay byte-compatible (no migration —
/// the store passes its own PascalCase options through to the writer).
/// </summary>
public class FileHabituationStoreTests
{
    [Fact]
    public void SaveLoad_RoundTripsOffset_AndOverwrites()
    {
        string path = Path.Combine(Path.GetTempPath(), $"hab-state-{Guid.NewGuid():N}.json");
        try
        {
            var store = new FileHabituationStore(path);

            store.Save(new HabituationState { Offset = 17.5 });
            var loaded = store.Load();
            Assert.NotNull(loaded);
            Assert.Equal(17.5, loaded!.Offset, 3);

            // second write exercises the File.Replace (existing-destination) path
            store.Save(new HabituationState { Offset = 22.0 });
            Assert.Equal(22.0, store.Load()!.Offset, 3);

            // no .tmp leaked after a successful write
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(path + ".tmp"); } catch { }
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        string path = Path.Combine(Path.GetTempPath(), $"hab-missing-{Guid.NewGuid():N}.json");
        var store = new FileHabituationStore(path);
        Assert.Null(store.Load());
    }
}
