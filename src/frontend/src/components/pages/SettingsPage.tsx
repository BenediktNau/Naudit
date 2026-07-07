import { useSettings } from "@/hooks/queries";
import { Panel } from "@/components/ui/Panel";
import { Pill } from "@/components/ui/Pill";

function Row({ label, value, on }: { label: string; value?: string; on?: boolean }) {
  return (
    <div className="flex items-center justify-between border-b border-hairline px-5 py-3.5 last:border-b-0">
      <span className="text-[13px] font-medium text-ink">{label}</span>
      {value !== undefined && <span className="font-mono text-[12.5px] text-ink2">{value}</span>}
      {on !== undefined && (on ? <Pill kind="ok">on</Pill> : <Pill kind="neutral">off</Pill>)}
    </div>
  );
}

/** Bewusst read-only (v1): zeigt die effektive Config, ändert nichts.
 *  Provider/Prompt werden weiterhin ausschließlich über appsettings/Env gesetzt. */
export function SettingsPage() {
  const { data, isLoading } = useSettings();
  if (isLoading || !data) return <div className="p-8 font-mono text-ink3">loading…</div>;

  return (
    <div className="flex flex-col gap-5 px-7 py-6">
      <div>
        <h2 className="font-mono text-lg font-bold">Settings</h2>
        <p className="mt-1 max-w-[70ch] text-[12.5px] text-ink3">
          Read-only view of the effective configuration. Values are set via <span className="font-mono">appsettings</span>
          /environment at deployment — not editable here.
        </p>
      </div>
      <div className="grid grid-cols-1 items-start gap-4 md:grid-cols-2">
        <Panel title="AI provider" extra="Naudit:Ai">
          <Row label="Provider" value={data.ai.provider} />
          <Row label="Model" value={data.ai.model} />
          <Row label="System prompt" value={data.systemPrompt} />
        </Panel>
        <Panel title="Git platform" extra="Naudit:Git">
          <Row label="Platform" value={data.git.platform} />
          {data.git.auth && <Row label="Bot identity" value={data.git.auth === "App" ? "GitHub App" : "PAT"} />}
          <Row label="Post real verdict" on={data.git.postVerdict} />
        </Panel>
        <Panel title="Sign-in methods" extra="Naudit:Ui:Auth">
          <Row label="Local (admin-provisioned)" on={true} />
          <Row label="GitHub OAuth" on={data.authMethods.gitHub} />
          <Row label="OIDC / Keycloak" on={data.authMethods.oidc} />
        </Panel>
      </div>
    </div>
  );
}
