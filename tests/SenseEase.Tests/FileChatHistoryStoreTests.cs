using System;
using System.IO;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Tests for the Phase-4 chat-transcript persistence store. The store is
/// best-effort (silently swallows I/O errors), so these tests focus on the
/// happy path plus the documented soft-failure modes.
/// </summary>
public class FileChatHistoryStoreTests : IDisposable
{
    private readonly string _path;

    public FileChatHistoryStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"chat_history_test_{Guid.NewGuid()}.jsonl");
    }

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { /* test cleanup */ }
        try { if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp"); } catch { /* test cleanup */ }
    }

    [Fact]
    public void Append_Then_Load_Round_Trips()
    {
        var store = new FileChatHistoryStore(_path);
        store.Append(new ChatMessage { Role = ChatRole.User, Content = "hello" });
        store.Append(new ChatMessage { Role = ChatRole.Assistant, Content = "hi there" });

        var loaded = store.Load();
        Assert.Equal(2, loaded.Count);
        Assert.Equal(ChatRole.User, loaded[0].Role);
        Assert.Equal("hello", loaded[0].Content);
        Assert.Equal(ChatRole.Assistant, loaded[1].Role);
        Assert.Equal("hi there", loaded[1].Content);
    }

    [Fact]
    public void Load_Empty_File_Returns_Empty()
    {
        File.WriteAllText(_path, string.Empty);
        var store = new FileChatHistoryStore(_path);
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Load_Missing_File_Returns_Empty()
    {
        // _path intentionally does NOT exist.
        Assert.False(File.Exists(_path));
        var store = new FileChatHistoryStore(_path);
        var ex = Record.Exception(() => store.Load());
        Assert.Null(ex);
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Load_Skips_Corrupt_Lines()
    {
        File.WriteAllLines(_path, new[]
        {
            "{ this is not valid json",
            "{\"Role\":0,\"Content\":\"ok\",\"Timestamp\":\"2026-01-01T00:00:00\"}",
            "garbage",
        });
        var store = new FileChatHistoryStore(_path);
        var loaded = store.Load();
        Assert.Single(loaded);
        Assert.Equal("ok", loaded[0].Content);
    }

    [Fact]
    public void Trim_Keeps_Most_Recent_N()
    {
        var store = new FileChatHistoryStore(_path);
        for (int i = 0; i < 5; i++)
            store.Append(new ChatMessage { Role = ChatRole.User, Content = $"msg{i}" });

        store.Trim(3);
        var loaded = store.Load();
        Assert.Equal(3, loaded.Count);
        Assert.Equal("msg2", loaded[0].Content);
        Assert.Equal("msg3", loaded[1].Content);
        Assert.Equal("msg4", loaded[2].Content);
    }

    [Fact]
    public void Append_Survives_Concurrent_Reads()
    {
        var store = new FileChatHistoryStore(_path);
        store.Append(new ChatMessage { Role = ChatRole.User, Content = "first" });
        // Interleave a read between appends — the in-progress file must remain readable.
        var mid = store.Load();
        Assert.Single(mid);

        store.Append(new ChatMessage { Role = ChatRole.Assistant, Content = "second" });
        var final = store.Load();
        Assert.Equal(2, final.Count);
        Assert.Equal("first", final[0].Content);
        Assert.Equal("second", final[1].Content);
    }
}
