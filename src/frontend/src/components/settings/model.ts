import type { ReactNode } from "react";

export type CategoryId = "instance" | "git" | "ai" | "review" | "signin";

export type WizardState = null | { kind: "github-signin" | "github-app" | "oidc"; step: 1 | 2 | 3 };

/** Zugriffs-Seam, den jede Kategorie bekommt. get() liest Draft → Wert → "". */
export interface SettingsCtx {
  get(key: string): string;
  set(key: string, value: string): void;
  locked(key: string): boolean;   // env-gesetzt (editable === false)
  secretSet(key: string): boolean;
  openWizard(w: NonNullable<WizardState>): void;
}

export interface CategoryMeta { id: CategoryId; label: string; title: string; blurb: string; }

/** Reihenfolge + Kopf-Texte der Kategorien (handoff §Screens). */
export const CATEGORIES: CategoryMeta[] = [
  { id: "instance", label: "Instance",     title: "Instance",     blurb: "Where Naudit lives and which repos it will review." },
  { id: "git",      label: "Git platform", title: "Git platform", blurb: "Connect the platform Naudit reads diffs from and posts reviews to." },
  { id: "ai",       label: "AI provider",  title: "AI provider",  blurb: "Pick the LLM that reviews your code. Only the fields it needs appear." },
  { id: "review",   label: "Review rules", title: "Review rules", blurb: "When a review blocks a merge, and the prompt that guides it." },
  { id: "signin",   label: "Sign-in",      title: "Sign-in",      blurb: "How admins and users sign in to this dashboard." },
];

export type ReactChildren = ReactNode; // Re-Export-Bequemlichkeit fuer Kategorien
