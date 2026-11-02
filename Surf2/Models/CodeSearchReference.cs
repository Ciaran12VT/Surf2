namespace Surf2.Models;

public sealed record CodeSearchReference(
    string DocumentPath,
    string SearchText,
    bool MatchCase = false,
    bool WholeWords = false,
    bool UseRegex = false);
