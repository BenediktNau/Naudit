using Naudit.Infrastructure.Redaction;
using Xunit;

namespace Naudit.Tests;

public class PatternRedactorTests
{
    private static readonly PatternRedactor Redactor = new(new RedactionOptions());

    private static async Task<string> Redact(string text) => await Redactor.RedactAsync(text);

    [Fact]
    public async Task AwsAccessKey_isRedactedAsToken()
    {
        var outp = await Redact("""var key = "AKIAIOSFODNN7EXAMPLE";""");
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", outp);
        Assert.Contains("«redacted:token»", outp);
    }

    [Fact]
    public async Task GitHubPat_isRedactedAsToken()
    {
        var outp = await Redact("token: ghp_abcdefghijklmnopqrstuvwxyz0123456789");
        Assert.DoesNotContain("ghp_abcdefghijklmnopqrstuvwxyz0123456789", outp);
        Assert.Contains("«redacted:", outp);
    }

    [Fact]
    public async Task Jwt_isRedactedAsToken()
    {
        // Aus Fragmenten zusammengesetzt, damit der Quelltext keinen vollständigen JWT enthält
        // (sonst schlagen Secret-Scanner auf die Test-Fixture an).
        var jwt = string.Concat(
            "eyJhbGciOiJIUzI1NiJ9", ".",
            "eyJzdWIiOiIxMjM0NTY3ODkwIn0", ".",
            "SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c");
        var outp = await Redact($"const t = \"{jwt}\";");
        Assert.DoesNotContain(jwt, outp);
        Assert.Contains("«redacted:token»", outp);
    }

    [Fact]
    public async Task PasswordAssignment_redactsOnlyTheValue_keepsPrefix()
    {
        var outp = await Redact("""password = "hunter2";""");
        Assert.DoesNotContain("hunter2", outp);
        Assert.Contains("password = ", outp);          // Prefix bleibt
        Assert.Contains("«redacted:secret»", outp);
    }

    [Fact]
    public async Task JsonStyleSecretKey_redactsValue_keepsQuotedKey()
    {
        // Recall: zitierter JSON-Key "password": "hunter2" muss ebenfalls greifen — der
        // kurze, niedrig-entropische Wert entkäme sonst dem Entropie-Fallback.
        var outp = await Redact("""  "password": "hunter2",""");
        Assert.DoesNotContain("hunter2", outp);
        Assert.Contains("\"password\": ", outp);        // zitierter Key bleibt erhalten
        Assert.Contains("«redacted:secret»", outp);
    }

    [Fact]
    public async Task KeywordSuffixInIdentifier_isNotOvermatched()
    {
        // Precision: "token" als Suffix in einem gewöhnlichen Bezeichner (authToken) darf die
        // Zuweisung nicht triggern; der zugewiesene Code-Wert ist kein Secret.
        const string code = "var authToken = lookupValue;";
        Assert.Equal(code, await Redact(code));
    }

    [Fact]
    public async Task Ipv4_isRedacted()
    {
        var outp = await Redact("""var host = "10.0.4.12";""");
        Assert.DoesNotContain("10.0.4.12", outp);
        Assert.Contains("«redacted:ip»", outp);
    }

    [Fact]
    public async Task Ipv6_isRedacted()
    {
        const string ip = "2001:0db8:85a3:0000:0000:8a2e:0370:7334";
        var outp = await Redact($"endpoint {ip}");
        Assert.DoesNotContain(ip, outp);
        Assert.Contains("«redacted:ip»", outp);
    }

    [Fact]
    public async Task Email_isRedacted()
    {
        var outp = await Redact("contact max.mustermann@firma.de for access");
        Assert.DoesNotContain("max.mustermann@firma.de", outp);
        Assert.Contains("«redacted:email»", outp);
    }

    [Fact]
    public async Task HighEntropyToken_isRedactedAsSecret()
    {
        const string blob = "XQj7KpLmN3rTvWxYz0aB4cDeFgHiJkLmNoPqRsTu"; // 40 Zeichen, hohe Entropie, kein Keyword-Kontext
        var outp = await Redact($"var blob = \"{blob}\";");
        Assert.DoesNotContain(blob, outp);
        Assert.Contains("«redacted:secret»", outp);
    }

    [Fact]
    public async Task PemPrivateKeyBlock_bodyRedacted_lineCountPreserved()
    {
        // "PRIVATE KEY" gesplittet, damit der Quelltext keinen vollständigen PEM-Header trägt
        // (Secret-Scanner schlagen sonst auf die Test-Fixture an); zur Laufzeit wieder identisch.
        var pem = string.Join('\n', new[]
        {
            "-----BEGIN RSA PRIVATE " + "KEY-----",
            "MIIEowIBAAKCAQEA7Yn5cVq8K3pLmN9rT2vWxYz0aB4cDeFgHiJkLmNoPqRsTuVw",
            "-----END RSA PRIVATE " + "KEY-----",
        });
        var outp = await Redact(pem);
        Assert.DoesNotContain("MIIEowIBAAKCAQEA7Yn5cVq8K3pLmN9rT2vWxYz0aB4cDeFgHiJkLmNoPqRsTuVw", outp);
        Assert.Equal(3, outp.Split('\n').Length);      // line-preserving
    }

    [Fact]
    public async Task NormalCode_isNotRedacted()
    {
        const string code = "var sum = a + b;";
        Assert.Equal(code, await Redact(code));
    }

    [Fact]
    public async Task LongWordIdentifier_isNotRedacted()
    {
        // Precision: langer, aber wort-artiger Identifier (keine Ziffer) darf nicht als Secret gelten.
        const string code = "var getUserAccountByEmailAddressService = null;";
        Assert.Equal(code, await Redact(code));
    }

    [Fact]
    public async Task VersionNumber_isNotMistakenForIp()
    {
        // Precision: vierteilige Versionsnummer mit Oktett > 255 ist keine IP.
        const string code = """var v = "10.0.19041.1";""";
        Assert.Equal(code, await Redact(code));
    }

    [Fact]
    public async Task DiffStructuralLines_areUntouched_andLineCountPreserved()
    {
        const string diff =
            "--- a/config.cs\n" +
            "+++ b/config.cs\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-var host = \"10.0.0.1\";\n" +
            "+var host = \"10.0.0.2\";";
        var outp = await Redact(diff);
        var lines = outp.Split('\n');

        Assert.Equal(5, lines.Length);                 // keine Zeile dazu/weg
        Assert.Equal("--- a/config.cs", lines[0]);     // Strukturzeilen unangetastet
        Assert.Equal("+++ b/config.cs", lines[1]);
        Assert.Equal("@@ -1,1 +1,1 @@", lines[2]);
        Assert.DoesNotContain("10.0.0.1", outp);       // Content-Zeilen redigiert
        Assert.DoesNotContain("10.0.0.2", outp);
        Assert.Contains("«redacted:ip»", outp);
    }

    [Fact]
    public async Task RawSecretValue_neverAppearsInOutput()
    {
        const string secret = "AKIAIOSFODNN7EXAMPLE";
        var outp = await Redact($"AWS_KEY={secret} more text {secret}");
        Assert.DoesNotContain(secret, outp);
    }

    [Fact]
    public async Task Disabled_viaNullRedactor_returnsTextUnchanged()
    {
        // Gegenprobe: der No-Op-Redactor (Aus-Fall) lässt alles durch.
        var nullRedactor = new Naudit.Core.Abstractions.NullPromptRedactor();
        const string secret = """password = "hunter2";""";
        Assert.Equal(secret, await nullRedactor.RedactAsync(secret));
    }
}
