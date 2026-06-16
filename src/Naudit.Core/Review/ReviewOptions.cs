namespace Naudit.Core.Review;

public sealed class ReviewOptions
{
    public string SystemPrompt { get; set; } = PromptBuilder.DefaultSystemPrompt;
}
