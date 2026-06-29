using Naudit.Core.Models;

namespace Naudit.Core.Review;

public sealed class ReviewOptions
{
    public string SystemPrompt { get; set; } = PromptBuilder.DefaultSystemPrompt;

    /// <summary>Severity-bewusste Gate-Policy (Naudit:Review:Gate).</summary>
    public ReviewGateOptions Gate { get; set; } = new();
}

/// <summary>Ab wann ein Review blockt (request_changes). Default: nur bestätigtes High/Critical.</summary>
public sealed class ReviewGateOptions
{
    /// <summary>Mindest-Schweregrad, ab dem ein Fund blocken kann. Default High.</summary>
    public FindingSeverity MinSeverity { get; set; } = FindingSeverity.High;

    /// <summary>Mindest-Sicherheit, ab der ein Fund blocken kann. Default Medium.</summary>
    public ReviewConfidence MinConfidence { get; set; } = ReviewConfidence.Medium;
}
