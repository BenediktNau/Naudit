namespace Naudit.Web;

/// <summary>Seam fuer HTTP im Setup-Modus: dort laeuft AddNauditInfrastructure nicht,
/// es gibt keinen IHttpClientFactory. Produktion = new HttpClient() (kurzlebige
/// Wizard-Einmal-Aufrufe), Tests injizieren einen Client mit StubHttpMessageHandler —
/// Muster analog AiTestClientFactory.</summary>
public sealed record SetupHttpClientFactory(Func<HttpClient> Create);
