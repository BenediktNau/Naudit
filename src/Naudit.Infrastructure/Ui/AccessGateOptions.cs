namespace Naudit.Infrastructure.Ui;

/// <summary>Naudit:AccessGate — Open (Default): jedes Projekt mit gültigem Webhook-Secret wird
/// reviewt (Pre-WebUI-Verhalten, typisch internes GitLab). Registered: nur Projekte aktiver
/// Accounts (EfAccessGate) — empfohlen für öffentlich installierbare GitHub Apps.</summary>
public enum AccessGateMode { Open, Registered }

public sealed class AccessGateOptions
{
    public AccessGateMode Mode { get; set; } = AccessGateMode.Open;
}
