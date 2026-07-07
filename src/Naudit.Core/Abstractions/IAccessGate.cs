namespace Naudit.Core.Abstractions;

/// <summary>Zugangsschranke: darf für dieses Projekt ein Review laufen?
/// DB-gestützte Implementierung in Infrastructure; bei deaktiviertem UI immer erlaubt.</summary>
public interface IAccessGate
{
    Task<bool> IsAllowedAsync(string projectId, CancellationToken ct = default);
}
