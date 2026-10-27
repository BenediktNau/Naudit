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
    public async Task EnvStyleAssignment_keyNameSurvives_andPublicShaStays()
    {
        // Regression aus PR #86: Naudit sah `ARG «redacted:secret»` und postete einen Medium-Fund
        // ("bitte bestätigen, dass kein Credential hardcodiert ist") — auf einen ÖFFENTLICHEN
        // Git-Commit-SHA. Ursache war nicht die Entropie-Schwelle: der SHA allein liegt bei 3.62
        // Bits/Zeichen, also unter 4.0. Erst weil `TokenLike` das '=' mitfrisst, wurde
        // `OPENGREP_RULES_REF=<sha>` EIN Token — Großbuchstaben + '_' + '=' + Hex heben die
        // Entropie auf 4.44. Der Schlüsselname, also genau der Kontext zur Beurteilung, ging
        // dabei mit verloren.
        const string line = "ARG OPENGREP_RULES_REF=f1d2b562b414783763fd02a6ed2736eaed622efa";
        Assert.Equal(line, await Redact(line));
    }

    [Fact]
    public async Task HighEntropyValue_isStillRedacted_evenBehindAnEnvStyleKey()
    {
        // Gegenprobe zum Test darüber: die Zuweisungsgrenze darf kein Schlupfloch werden. Der
        // WERT wird weiterhin auf eigene Rechnung geprüft — nur der Schlüssel zählt nicht mehr mit.
        const string secret = "XQj7KpLmN3rTvWxYz0aB4cDeFgHiJkLmNoPqRsTu";
        var outp = await Redact($"ARG BUILD_CREDENTIAL={secret}");

        Assert.DoesNotContain(secret, outp);
        Assert.Contains("«redacted:secret»", outp);
        Assert.Contains("BUILD_CREDENTIAL", outp);   // Schlüssel bleibt lesbar
    }

    [Fact]
    public async Task Base64PaddedToken_isStillRedacted()
    {
        // '=' darf nicht komplett aus der Token-Klasse fallen: als Base64-Padding gehört es ans
        // ENDE eines Tokens und muss weiterhin mitmaskiert werden, sonst bliebe ein '=='-Rest stehen.
        const string blob = "aGVsbG8gd29ybGQgc2VjcmV0IHZhbHVlIDEyMzQ1Ng==";
        var outp = await Redact($"var blob = \"{blob}\";");

        Assert.DoesNotContain(blob, outp);
        Assert.DoesNotContain("==", outp);
        Assert.Contains("«redacted:secret»", outp);
    }

    [Fact]
    public async Task PrefixedSecretKey_isRedacted_viaAssignmentRule()
    {
        // Kurze Secrets kann der Entropie-Pass grundsaetzlich nicht fangen: bei einer Schwelle von
        // 4.0 Bits/Zeichen erreicht ein Token unter 16 Zeichen sie nie (log2(16) = 4.0 bei lauter
        // verschiedenen Zeichen). Zustaendig ist allein die Keyword-Regel — und die hatte eine
        // Luecke: die linke Grenze (?<![\w-]) schloss '_' ein, also blieb jeder praefixierte
        // Schluessel aussen vor. Genau die Schreibweise, die in .env/Dockerfiles ueblich ist.
        var outp = await Redact("DB_PASSWORD=hunter2");

        Assert.DoesNotContain("hunter2", outp);
        Assert.Contains("DB_PASSWORD", outp);          // Schluessel bleibt lesbar
        Assert.Contains("«redacted:secret»", outp);
    }

    [Fact]
    public async Task PrefixedCredentialKey_isRedacted()
    {
        // Aufgefallen an Naudits Fund zu diesem PR: `MY_DB_CREDENTIAL=<kurz>` war vorher nur
        // deshalb maskiert, weil die Schluesselzeichen die Entropie hochzogen — Erkennung aus dem
        // falschen Grund. Jetzt greift die Keyword-Regel, die den Schluessel stehen laesst.
        var outp = await Redact("ARG MY_DB_CREDENTIAL=n7Bq9Km2");

        Assert.DoesNotContain("n7Bq9Km2", outp);
        Assert.Contains("MY_DB_CREDENTIAL", outp);
        Assert.Contains("«redacted:secret»", outp);
    }

    [Fact]
    public async Task HighEntropyKey_doesNotSwallowTheAssignmentDelimiter()
    {
        // Loch im ersten Anlauf dieses PRs, von CodeRabbit gefunden: `={0,2}` war als
        // Base64-Padding gedacht, nahm aber auch den ZUWEISUNGS-Trenner mit. Ein hochentropischer
        // SCHLUESSEL matchte damit samt '=' (`aB3d…qRs7=`) und wurde maskiert — der Trenner
        // verschwand, und die zugesicherte Zuweisungsgrenze war genau dann unwahr, wenn sie zaehlt.
        const string line = "ARG aB3dEf9HjKl2MnP4qRs7=publicvalue";
        var outp = await Redact(line);

        Assert.Equal(line, outp);
        Assert.Contains("=", outp);   // Trenner ueberlebt
    }

    [Fact]
    public async Task AmbiguousCredentialsKeyword_withWordValue_isNotRedacted()
    {
        // Aus Naudits zweiter Runde zu #87: `credentials` ist im Web-Umfeld ueberwiegend KEIN
        // Secret-Schluessel. Ein Wort-Wert ("include", true) ist nie ein Secret — hier zu maskieren
        // waere genau die Ueber-Maskierung, die dieser PR abstellt.
        const string js = "fetch(url, { credentials: 'include' })";
        const string cors = "app.use(cors({ credentials: true }))";

        Assert.Equal(js, await Redact(js));
        Assert.Equal(cors, await Redact(cors));
    }

    [Fact]
    public async Task AmbiguousCredentialsKeyword_withSecretLikeValue_isStillRedacted()
    {
        // Gegenprobe: sieht der Wert nach Secret aus (Ziffern UND Buchstaben, lang genug),
        // bleibt `credential` ein Treffer — sonst waere die Abdeckung aus f5b55b9 wieder weg.
        var outp = await Redact("ARG MY_DB_CREDENTIAL=n7Bq9Km2");

        Assert.DoesNotContain("n7Bq9Km2", outp);
        Assert.Contains("«redacted:secret»", outp);
    }

    [Fact]
    public async Task InlineCodeInProse_isNotSwallowed()
    {
        // Der Wert darf nicht ueber den schliessenden Backtick hinauslaufen: sonst frisst die Regel
        // in Doku/Prosa den Code-Span und hinterlaesst unbalancierte Backticks. Aufgefallen, weil
        // Naudit genau das an docs/redaction.md meldete — im Quelltext war die Zeile korrekt, in der
        // REDIGIERTEN Fassung nicht mehr.
        const string prose = "prefixed keys like `DB_PASSWORD=` or `x-token:` are covered";
        var outp = await Redact(prose);

        Assert.Equal(prose, outp);
        Assert.Equal(4, outp.Count(c => c == '`'));   // beide Code-Spans intakt
    }

    [Fact]
    public async Task RealHeaderValue_isStillRedacted_despiteBacktickGuard()
    {
        // Die Backtick-Grenze darf kein Schlupfloch sein: ein echter Header-Wert wird weiterhin
        // maskiert, der Schluessel bleibt lesbar.
        var outp = await Redact("x-token: aB3dEf9HjKl");

        Assert.DoesNotContain("aB3dEf9HjKl", outp);
        Assert.Contains("x-token", outp);
        Assert.Contains("«redacted:secret»", outp);
    }

    [Fact]
    public async Task IdentifierEndingInKeywordButNotAssigned_isNotRedacted()
    {
        // Gegenprobe zur gelockerten Grenze: der Schluessel muss unmittelbar vor dem Trenner enden.
        // `secret_flag` und ein camelCase-`authToken` duerfen weiterhin nicht greifen.
        const string code = "var secret_flag = true; var authToken = getAuthToken();";
        Assert.Equal(code, await Redact(code));
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

    // ---- Token-Präfixe der gängigen Plattformen (Security-Härtung) ----
    // Fixtures werden aus Fragmenten zusammengesetzt, damit der Quelltext selbst keine
    // vollständigen Token-Literale trägt (Secret-Scanner/Push-Protection schlagen sonst an).

    public static TheoryData<string> PrefixedTokens() => new()
    {
        "gh" + "p_" + "aB3dEf9HjKl2MnP4qRs7tUv8wXy0zA1bC2dE",       // GitHub PAT classic
        "gh" + "o_" + "aB3dEf9HjKl2MnP4qRs7tUv8wXy0zA1bC2dE",       // GitHub OAuth
        "gh" + "u_" + "aB3dEf9HjKl2MnP4qRs7tUv8wXy0zA1bC2dE",       // GitHub user-to-server
        "gh" + "s_" + "aB3dEf9HjKl2MnP4qRs7tUv8wXy0zA1bC2dE",       // GitHub server-to-server (App)
        "gh" + "r_" + "aB3dEf9HjKl2MnP4qRs7tUv8wXy0zA1bC2dE",       // GitHub refresh
        "github" + "_pat_" + "11ABCDEFG0aB3dEf9HjKl2_MnP4qRs7tUv8wXy0zA1bC2dE",
        "gl" + "pat-" + "aB3dEf9HjKl2MnP4qRs7",                     // GitLab PAT
        "gl" + "dt-" + "aB3dEf9HjKl2MnP4qRs7",                      // GitLab deploy token
        "gl" + "rt-" + "aB3dEf9HjKl2MnP4qRs7",                      // GitLab runner token
        "gl" + "ptt-" + "aB3dEf9HjKl2MnP4qRs7tUv8wXy0",             // GitLab pipeline trigger
        "sk-" + "ant-api03-" + "aB3dEf9HjKl2MnP4qRs7tUv8wXy0zA1bC2dE",  // Anthropic
        "sk-" + "proj-" + "aB3dEf9HjKl2MnP4qRs7tUv8wXy0zA1bC2dE",       // OpenAI project key
        "sk-" + "aB3dEf9HjKl2MnP4qRs7tUv8wXy0zA1bC2dE",                 // OpenAI legacy key
        "xo" + "xb-" + "1234567890-abcdefghij",                     // Slack bot
        "AK" + "IA" + "IOSFODNN7EXAMPLE",                           // AWS access key id
        "AS" + "IA" + "IOSFODNN7EXAMPLE",                           // AWS temporary key id
    };

    [Theory]
    [MemberData(nameof(PrefixedTokens))]
    public async Task PrefixedPlatformToken_isRedacted_evenWithoutKeywordContext(string token)
    {
        // Ohne Schlüsselwort davor (kein "token="), damit wirklich die Präfix-Regel greifen muss.
        var outp = await Redact($"curl -H \"X-Foo: {token}\" https://example.invalid");
        Assert.DoesNotContain(token, outp);
        Assert.Contains("«redacted:", outp);
    }

    [Fact]
    public async Task PrefixRules_doNotHitOrdinaryHyphenatedWords()
    {
        // Precision: "sk-"/"gl…-" mitten in normalen Bezeichnern (CSS-Klassen, Task-Namen) ist kein Token.
        const string code = "<div class=\"desk-layout task-runner glow-effect\"></div>";
        Assert.Equal(code, await Redact(code));
    }

    [Fact]
    public async Task PemBlock_multiLineBody_isFullyRedacted_evenWithLowEntropyLines()
    {
        // Der Entropie-Fallback allein reicht nicht: eine Body-Zeile ohne Ziffern (oder eine kurze
        // Schlusszeile) rutschte durch. Die Block-Erkennung maskiert alles zwischen BEGIN und END.
        var body1 = "MIIEowIBAAKCAQEA7Yn5cVq8K3pLmN9rT2vWxYz0aB4cDeFgHiJkLmNoPqRsTuVw";
        var body2 = "abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijkl"; // keine Ziffer
        var body3 = "q8K3pLmN==";                                                         // kurz
        var pem = string.Join('\n', new[]
        {
            "+-----BEGIN PRIVATE " + "KEY-----",
            "+" + body1,
            "+" + body2,
            "+" + body3,
            "+-----END PRIVATE " + "KEY-----",
            "+var next = 1;",
        });
        var outp = await Redact(pem);
        var lines = outp.Split('\n');

        Assert.Equal(6, lines.Length);                 // line-preserving
        Assert.DoesNotContain(body1, outp);
        Assert.DoesNotContain(body2, outp);
        Assert.DoesNotContain(body3, outp);
        Assert.All(lines, l => Assert.StartsWith("+", l));   // Diff-Marker bleibt stehen
        Assert.Equal("+var next = 1;", lines[5]);       // Block endet am END
    }

    [Fact]
    public async Task PemBlock_onOneLine_withEscapedNewlines_isRedacted()
    {
        // JSON/.env-Schreibweise: der ganze Schlüssel steht mit "\n"-Escapes in EINER Zeile.
        var key = "MIIEowIBAAKCAQEA7Yn5cVq8K3pLmN9rT2vWxYz0aB4cDeFgHiJkLmNoPqRsTuVw";
        var line = "\"privateKey\": \"-----BEGIN RSA PRIVATE " + "KEY-----\\n" + key
                   + "\\nabcdefghijkl\\n-----END RSA PRIVATE " + "KEY-----\\n\",";
        var outp = await Redact(line);

        Assert.DoesNotContain(key, outp);
        Assert.DoesNotContain("abcdefghijkl", outp);
        Assert.Contains("«redacted:private-key»", outp);
    }

    [Fact]
    public async Task PemHeaderAsCodeConstant_doesNotSwallowFollowingCode()
    {
        // Gegenprobe: ein Parser, der den Header als Konstante kennt, darf nicht den Rest der
        // Datei verschlucken — der Block endet an der ersten Zeile, die kein PEM-Body ist.
        var code = string.Join('\n', new[]
        {
            "+const string Header = \"-----BEGIN PRIVATE " + "KEY-----\";",
            "+var parsed = Parse(input);",
            "+return parsed;",
        });
        var outp = await Redact(code);
        var lines = outp.Split('\n');

        Assert.EndsWith("\";", lines[0]);              // String-Literal bleibt geschlossen
        Assert.Equal("+var parsed = Parse(input);", lines[1]);
        Assert.Equal("+return parsed;", lines[2]);
    }

    // ---- Suffix-Schlüssel (WebhookSecret=, ClientSecret=, ApiKey:) ----

    [Theory]
    [InlineData("WebhookSecret=hunter2", "WebhookSecret=", "hunter2")]
    [InlineData("Naudit__GitHub__WebhookSecret=s3cr3tv", "Naudit__GitHub__WebhookSecret=", "s3cr3tv")]
    [InlineData("ClientSecret = \"abcDEF\"", "ClientSecret = ", "abcDEF")]
    [InlineData("  ApiKey: q7Wm2Zp", "ApiKey: ", "q7Wm2Zp")]
    [InlineData("\"OpenAiApiKey\": \"short1\",", "\"OpenAiApiKey\": ", "short1")]
    [InlineData("gitlabAccessToken: \"xyz\"", "gitlabAccessToken: ", "xyz")]
    [InlineData("ADMIN_PASSWORD=plain", "ADMIN_PASSWORD=", "plain")]
    [InlineData("dbPassword=plain", "dbPassword=", "plain")]
    public async Task SuffixStyleSecretKey_redactsOnlyTheValue(string line, string keyPart, string value)
    {
        var outp = await Redact(line);
        Assert.DoesNotContain(value, outp);
        Assert.Contains(keyPart, outp);                 // Schlüsselname bleibt vollständig lesbar
        Assert.Contains("«redacted:secret»", outp);
    }

    [Theory]
    [InlineData("var secret = GetSecret();")]
    [InlineData("var secret = await vault.GetSecretAsync(name, ct);")]
    [InlineData("string? WebhookSecret { get; set; }")]
    [InlineData("public string WebhookSecret { get; set; } = \"\";")]
    [InlineData("options.WebhookSecret = request.WebhookSecret;")]
    [InlineData("var clientSecret = options.ClientSecret;")]
    [InlineData("await client.SendAsync(msg, cancellationToken: ct);")]
    [InlineData("const { accessToken: token } = session;")]
    [InlineData("var authToken = lookupValue;")]
    [InlineData("if (apiKey == null) return;")]
    [InlineData("WebhookSecret: null")]
    [InlineData("var hasToken = true;")]
    [InlineData("The webhook secret is validated in constant time.")]
    [InlineData("var token = context.Request.Headers[\"X-Gitlab-Token\"].ToString();")]
    [InlineData("<StepAi hasAiApiKey={hasAiApiKey} update={update} />")]
    [InlineData("-e Naudit__GitHub__WebhookSecret=<random-secret> \\")]
    [InlineData("NAUDIT_TOKEN: ${{ secrets.NAUDIT_TOKEN }}")]
    [InlineData("var token = await tokens.ResolveTokenAsync(request.ProjectId, ct);")]
    [InlineData("Set `WebhookSecret` in the settings text field.")]
    public async Task CodeMentioningSecretKeys_isNotRedacted(string code)
    {
        // Regression (Dogfooding PR #50): maskiert wurde u. a. der Wert von `secret = GetSecret()` —
        // die Review sah dann «redacted:secret» im Code und meldete ein hart codiertes Secret.
        // Code-Ausdrücke (Aufrufe, Member-Zugriffe, Literale wie null/true) sind nie Secrets.
        Assert.Equal(code, await Redact(code));
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
