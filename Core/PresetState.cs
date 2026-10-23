namespace KClevoPanel.Core;

public sealed record PresetState(int? Mode, bool Available, string Status, string Backend = "DirectDchu",
    string Verification = "SavedSelection", string? StateToken = null, string? JournalPath = null);
