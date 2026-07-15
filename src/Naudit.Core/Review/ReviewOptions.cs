using Naudit.Core.Models;

namespace Naudit.Core.Review;

public sealed class ReviewOptions
{
    public string SystemPrompt { get; set; } = PromptBuilder.DefaultSystemPrompt;

    /// <summary>Severity-bewusste Gate-Policy (Naudit:Review:Gate).</summary>
    public ReviewGateOptions Gate { get; set; } = new();

    /// <summary>Kontext-Anreicherung aus dem Checkout (Naudit:Review:Context).</summary>
    public ReviewContextOptions Context { get; set; } = new();

    /// <summary>Max. automatische (Webhook-)Reviews pro MR/PR; danach werden Pushes übersprungen.
    /// 0 (oder negativ) = unbegrenzt. Der CI-Trigger (POST /review) ist nie limitiert.</summary>
    public int MaxRoundtrips { get; set; } = 3;
}

/// <summary>Ab wann ein Review blockt (request_changes). Default: nur bestätigtes High/Critical.</summary>
public sealed class ReviewGateOptions
{
    /// <summary>Mindest-Schweregrad, ab dem ein Fund blocken kann. Default High.</summary>
    public FindingSeverity MinSeverity { get; set; } = FindingSeverity.High;

    /// <summary>Mindest-Sicherheit, ab der ein Fund blocken kann. Default Medium.</summary>
    public ReviewConfidence MinConfidence { get; set; } = ReviewConfidence.Medium;
}

/// <summary>Steuert die Kontext-Anreicherung: Umfang, Budget, Heuristik-Grenzen. Default AN.</summary>
public sealed class ReviewContextOptions
{
    /// <summary>Kontext-Sektion überhaupt bauen. Default true; false ⇒ heutiges diff-only-Verhalten.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gesamtbudget (Zeichen) für die Kontext-Sektion. Priorität Umgebung &gt; Call-Sites &gt; Überblick.</summary>
    public int MaxChars { get; set; } = 40_000;

    /// <summary>Datei ≤ dieser Zeilenzahl ⇒ ganze Datei; größer ⇒ Block-Heuristik um die Hunks.</summary>
    public int FullFileMaxLines { get; set; } = 400;

    /// <summary>Mindest-Fallback-Fenster ± Zeilen um einen Hunk-Anker, falls die Block-Heuristik zu eng ist.</summary>
    public int BlockPadLines { get; set; } = 30;

    /// <summary>± Zeilen Umgebung um eine Call-Site.</summary>
    public int UsageSnippetLines { get; set; } = 3;

    /// <summary>Maximale Call-Sites je Symbol.</summary>
    public int MaxUsagesPerSymbol { get; set; } = 5;

    /// <summary>Tiefe des Verzeichnisbaums im Überblick.</summary>
    public int MaxTreeDepth { get; set; } = 3;

    /// <summary>Kopf-Zeilen der README im Überblick.</summary>
    public int ReadmeMaxLines { get; set; } = 50;
}
