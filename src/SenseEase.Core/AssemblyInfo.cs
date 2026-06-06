using System.Runtime.CompilerServices;

// Expose SenseEase.Core internals to the test project for direct unit coverage of
// pure helpers (e.g. DeterministicSetSelector.Fit / ManualConflict, intensity-stats
// math). Keeps those helpers `internal` (out of the public API) while still unit-testable
// — the pattern the codebase already references (HabituationOverrideParser remarks).
[assembly: InternalsVisibleTo("SenseEase.Tests")]
