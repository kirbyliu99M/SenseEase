using SenseEase.Core.Models;

namespace SenseEase.Core.Abstractions;

/// <summary>
/// UM3 (User Model sprint): persistence facade for the passive user-model state
/// (the analysis index + bucket manifest in <c>user_model_index.json</c>). Later
/// UM-series PRs extend this interface — UM2 adds observation-bucket access and
/// H24 adds <c>ClearUserModel()</c> for the "clear all my data" path — but PR-A
/// introduces only the index read/write surface so the storage layer can land
/// without the schema migration.
/// </summary>
public interface IUserModelStore
{
    /// <summary>Load the index, or a fresh zero-state instance when the file is
    /// missing / unreadable. Never returns null.</summary>
    UserModelIndex LoadIndex();

    /// <summary>Atomically persist the index (tmp + File.Replace). Best-effort —
    /// never throws.</summary>
    void SaveIndex(UserModelIndex index);
}
