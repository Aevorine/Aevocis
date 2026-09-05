namespace OpenSuperWhisper.Core.Models;

/// <summary>
/// F33 term-dictionary self-learning: one (original recognized fragment -> user's replacement)
/// pair observed from an edit the user made inside the F11 draft-confirm window, and how many
/// separate dictations it's been seen in so far. Lives only in the small "pending candidates"
/// store (<c>OpenSuperWhisper.Storage.TermLearningStore</c>) - once <see cref="Count"/> reaches
/// <see cref="TermLearning.PromotionThreshold"/> the pair is promoted into the real term
/// dictionary (<c>TermCorrection</c> via <c>TermDictionaryStore</c>) and removed from here.
/// </summary>
public sealed class ObservedTermEdit
{
    public string Original { get; set; } = "";
    public string Replacement { get; set; } = "";
    public int Count { get; set; }
}
