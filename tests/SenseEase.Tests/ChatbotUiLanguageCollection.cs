using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Serializes every test that READS or WRITES the process-global chatbot UI-language pin
/// (<c>SenseEase.Infrastructure.Real.ChatbotUiLanguage.IsEnglishProvider</c>).
///
/// <para>The pin is a mutable static. Production reads it at leaf points
/// (<c>ReplyComplianceScrubber.Scrub</c>'s output-language backstop + <c>IsEnglishReply</c>,
/// <c>FeedbackLexicon.PreferZh</c> → <c>BuildFallbackResponse</c>/<c>TryBuildFollowup</c>,
/// <c>NluComplianceGate</c>'s question-trap line). Tests that flip the pin via
/// <c>IsEnglishProvider</c> (e.g. <c>BuildLanguagePinTests</c>) would, under xUnit's default
/// cross-collection parallelism, leak their setting into a concurrently-running test that asserts
/// a language-specific reply — exactly the class of transient race this project has been bitten by
/// before. Grouping all readers + writers into ONE collection makes them run sequentially, so the
/// pin is always the value the running test expects (writers set/restore in a finally; readers see
/// the restored null). Add any NEW test class that calls Scrub / NluComplianceGate.Check /
/// BuildFallbackResponse / TryBuildFollowup with a language-sensitive assertion to this collection.</para>
/// </summary>
[CollectionDefinition("ChatbotUiLanguage")]
public sealed class ChatbotUiLanguageCollection { }
