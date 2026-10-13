namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// Per-model token prices used to estimate cost in the token &amp; cost dashboard (roadmap #9).
/// Values are in your billing currency <b>per 1,000,000 tokens</b> (the unit most providers quote).
/// Supplied by the developer via <c>MentorOptions.ModelPricing</c>; MentorAgent ships no built-in
/// price table, so cost is shown only for models you price here.
/// </summary>
/// <param name="InputPer1M">Price per 1,000,000 input (prompt) tokens.</param>
/// <param name="OutputPer1M">Price per 1,000,000 output (completion) tokens.</param>
public sealed record ModelPrice(decimal InputPer1M, decimal OutputPer1M);
