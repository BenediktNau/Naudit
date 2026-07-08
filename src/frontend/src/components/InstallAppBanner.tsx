import { useGitHubApp } from "@/hooks/queries";

/** Onboarding-Banner: erscheint, solange mindestens ein verknüpfter GitHub-Login die Naudit-App
 *  noch nicht installiert hat. Feature aus / Fehler / alles installiert ⇒ nichts. */
export function InstallAppBanner() {
  const { data } = useGitHubApp();
  if (!data || !data.installUrl) return null;
  const missing = data.accounts.filter((a) => a.installed === false);
  if (missing.length === 0) return null;

  return (
    <div className="flex flex-wrap items-center gap-4 rounded-xl border border-acc/40 bg-acc/10 px-5 py-4">
      <span className="min-w-0 flex-1 text-sm leading-relaxed text-ink">
        Naudit isn’t installed on your GitHub {missing.length === 1 ? "account" : "accounts"} yet
        {" — "}
        <span className="font-mono">{missing.map((a) => a.login).join(", ")}</span>. Install it so
        reviews start running on your repositories.
      </span>
      <a
        href={data.installUrl}
        target="_blank"
        rel="noreferrer"
        className="shrink-0 cursor-pointer rounded-lg bg-acc px-4 py-2 text-sm font-bold text-accink transition-colors hover:bg-acc2 focus-visible:outline-2 focus-visible:outline-solid focus-visible:outline-offset-2 focus-visible:outline-teal"
      >
        Install on GitHub
      </a>
    </div>
  );
}
