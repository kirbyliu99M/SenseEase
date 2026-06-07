using System;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Storage;

/// <summary>
/// UM3 (User Model sprint): file-backed <see cref="IUserModelStore"/> for
/// <c>user_model_index.json</c>. Atomic write via <see cref="AtomicJsonWriter"/>
/// (tmp + File.Replace, snake_case wire format) — the same crash-safe pattern as
/// habituation_profile.json. A missing / unreadable file loads as a fresh
/// zero-state <see cref="UserModelIndex"/> (exportable=false, deletable_on_clear=true).
/// </summary>
public sealed class UserModelIndexStore : IUserModelStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public UserModelIndexStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public UserModelIndex LoadIndex()
    {
        lock (_gate)
        {
            return AtomicJsonWriter.TryRead<UserModelIndex>(_path) ?? new UserModelIndex();
        }
    }

    public void SaveIndex(UserModelIndex index)
    {
        if (index is null) return;
        lock (_gate)
        {
            AtomicJsonWriter.TryWrite(_path, index);
        }
    }
}
