import type { CategoryId, SettingsCtx } from "./model";

/** Status-Hinweise pro Kategorie aus den effektiven Settings (handoff §Interactions). */
export function computeHints(ctx: SettingsCtx): Record<CategoryId, { tone: "acc" | "ink3" | "warn"; text: string }> {
  const platform = ctx.get("Naudit:Git:Platform") || "GitLab";
  const isGitHub = platform === "GitHub";
  const usesApp = ctx.get("Naudit:GitHub:Auth") === "App";
  const gitConnected = isGitHub
    ? (usesApp ? ctx.secretSet("Naudit:GitHub:App:PrivateKey") : ctx.secretSet("Naudit:GitHub:Token")) && ctx.secretSet("Naudit:GitHub:WebhookSecret")
    : ctx.secretSet("Naudit:GitLab:Token") && ctx.secretSet("Naudit:GitLab:WebhookSecret");

  const provider = ctx.get("Naudit:Ai:Provider") || "Ollama";
  const providerLabel = provider === "ClaudeCode" ? "Claude Code"
    : provider === "OpenAICompatible" ? "OpenAI-compat" : provider;

  const sev = ctx.get("Naudit:Review:Gate:MinSeverity");
  const conf = ctx.get("Naudit:Review:Gate:MinConfidence");
  const gateDefault = (!sev || sev === "High") && (!conf || conf === "Medium");

  const gh = ctx.get("Naudit:Ui:Auth:GitHub:Enabled") === "true";
  const oidc = ctx.get("Naudit:Ui:Auth:Oidc:Enabled") === "true";

  return {
    instance: ctx.get("Naudit:PublicBaseUrl")
      ? { tone: "acc", text: "✓" } : { tone: "warn", text: "not set" },
    git: gitConnected ? { tone: "acc", text: `✓ ${platform}` } : { tone: "ink3", text: platform },
    ai: { tone: "ink3", text: providerLabel },
    review: gateDefault ? { tone: "ink3", text: "defaults" } : { tone: "ink3", text: "custom" },
    signin: gh ? { tone: "acc", text: "GitHub" } : oidc ? { tone: "acc", text: "SSO" } : { tone: "warn", text: "local only" },
  };
}
