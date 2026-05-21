using SenseEase.Core.Models;

namespace SenseEase.Core.Abstractions;

/// <summary>
/// Persistence for the display baseline, so a force-killed session (where restore
/// never ran) can be recovered and undone on the next launch. Best-effort — never throws.
/// </summary>
public interface IDisplayBaselineStore
{
    DisplayBaselineRecord? Load();
    void Save(DisplayBaselineRecord record);
    void Clear();
}
