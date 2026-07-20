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

    /// <summary>Projekt-Gedächtnis: FPs + Konventionen als Prompt-Guidance (Naudit:Review:Memory).</summary>
    public ReviewMemoryOptions Memory { get; set; } = new();

    /// <summary>Finding-Resolution-Tracking (Review-Analytics, Naudit:Review:Resolution).</summary>
    public ReviewResolutionOptions Resolution { get; set; } = new();

    /// <summary>Architektur-Profil: destillierte Projekt-Guidelines (Naudit:Review:Guidelines).</summary>
    public ReviewGuidelinesOptions Guidelines { get; set; } = new();
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

/// <summary>Projekt-Gedächtnis. Default AN; Enabled=false ⇒ No-Op-Selector (heutiges Verhalten).</summary>
public sealed class ReviewMemoryOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Deckel für die Prompt-Sektion — Konventionen zuerst, dann FPs, je neueste zuerst.</summary>
    public int MaxEntries { get; set; } = 50;
}

/// <summary>Review-Analytics: Erfassung der Finding-Auflösung. Enabled=false ⇒ keine Signal-Erfassung
/// (Webhooks antworten trotzdem 200); der Analytics-Endpoint bleibt lesbar.</summary>
public sealed class ReviewResolutionOptions
{
    public bool Enabled { get; set; } = true;
    public bool LlmClassification { get; set; } = true;   // Freitext-Klassifikation (PR 4)
    public bool RenderCheckbox { get; set; } = true;       // GitHub-Checkbox-Footer (PR 4)
}

/// <summary>Architektur-Profil. Default AN; Enabled=false ⇒ NullReviewGuidelines (heutiges Verhalten).</summary>
public sealed class ReviewGuidelinesOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Deckel für den Destillat-Input (Summe der Quelldateien; übergroße Dateien werden ganz übersprungen).</summary>
    public int MaxSourceChars { get; set; } = 60_000;

    /// <summary>Deckel für das gespeicherte/eingespeiste Profil.</summary>
    public int MaxProfileChars { get; set; } = 4_000;

    /// <summary>Quellen relativ zum Repo-Root; Reihenfolge = Priorität. Exakte Namen oder das Muster "dir/**/*.md".</summary>
    public List<string> Sources { get; set; } =
        ["CLAUDE.md", "AGENTS.md", "README.md", "CONTRIBUTING.md", "docs/**/*.md"];
}
