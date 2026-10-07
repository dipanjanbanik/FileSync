namespace FileSync
{
    internal enum SynchronizationMode
    {
        ReplicateSource,
        ReplicateDestination,
        KeepDestinationAndAddSourceChanges,
        KeepSourceAndAddDestinationChanges
    }

    internal sealed record SynchronizationRule(string Side, string Status, string Action, string? Counter = null);

    internal sealed record SynchronizationRuleOption(
        SynchronizationMode Mode,
        string Name,
        IReadOnlyList<SynchronizationRule> Rules)
    {
        public string RuleSummary => Rules.Count == 0
            ? "Rules are not implemented for this option."
            : string.Join(Environment.NewLine, Rules.Select(rule =>
                $"{rule.Status} on {rule.Side} — {rule.Action}"
                + (rule.Counter is null ? string.Empty : $" (count as {rule.Counter})")));
    }

    internal static class SynchronizationRules
    {
        public static IReadOnlyList<SynchronizationRuleOption> Options { get; } =
        [
            new(SynchronizationMode.ReplicateSource, "Replicate source",
            [
                new("Source", "Missing", "Do nothing"),
                new("Destination", "Missing", "Copy from Source", "new"),
                new("Source", "Only here", "Copy to destination"),
                new("Destination", "Only here", "Delete on Destination", "deleted"),
                new("Source", "Different", "Copy to destination"),
                new("Destination", "Different", "Overwrite on Destination", "overwritten"),
                new("Source", "Matched", "Skip"),
                new("Destination", "Matched", "Skip", "skipped"),
                new("Either side", "Type differs", "Skip and report conflict")
            ]),
            new(SynchronizationMode.ReplicateDestination, "Replicate destination", []),
            new(SynchronizationMode.KeepDestinationAndAddSourceChanges,
                "Keep destination files and add changes from the source", []),
            new(SynchronizationMode.KeepSourceAndAddDestinationChanges,
                "Keep source files and add changes from the destination", [])
        ];
    }
}
