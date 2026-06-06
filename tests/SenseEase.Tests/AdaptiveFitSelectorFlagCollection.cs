using Xunit;

namespace SenseEase.Tests;

/// <summary>Serialises the test classes that mutate the process-wide static
/// <c>AppConfig.AdaptiveFitSelectorEnabled</c>. Without this, a flag-ON test in one
/// class (which flips the static true inside a try/finally) runs in PARALLEL with a
/// "defaults-false" / flag-OFF assertion in a sibling class and the reader observes
/// the mid-flight <c>true</c> — an intermittent failure. xUnit runs every test in a
/// single collection sequentially, so tagging both flag-touching classes into this
/// collection removes the race without disabling parallelism for the rest of the suite.</summary>
[CollectionDefinition("AdaptiveFitSelectorFlag")]
public sealed class AdaptiveFitSelectorFlagCollection { }
