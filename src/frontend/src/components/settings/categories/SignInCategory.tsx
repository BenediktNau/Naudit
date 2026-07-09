import { Pill } from "@/components/ui/Pill";
import { Button } from "@/components/ui/Button";
import type { ReactNode } from "react";
import type { SettingsCtx } from "../model";

function Card({ title, pill, children, action }: { title: string; pill: ReactNode; children: ReactNode; action: ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-4 rounded-xl border border-hairline bg-surface px-5 py-5">
      <div className="min-w-0">
        <div className="flex items-center gap-2">
          <b className="text-[14px]">{title}</b>
          {pill}
        </div>
        <div className="mt-1 max-w-[64ch] text-[12.5px] text-ink2">{children}</div>
      </div>
      <div className="shrink-0">{action}</div>
    </div>
  );
}

export function SignInCategory({ ctx }: { ctx: SettingsCtx }) {
  const gh = ctx.get("Naudit:Ui:Auth:GitHub:Enabled") === "true";
  const oidc = ctx.get("Naudit:Ui:Auth:Oidc:Enabled") === "true";

  return (
    <div className="flex flex-col gap-3">
      <Card title="GitHub sign-in" pill={gh ? <Pill kind="ok">✓ on</Pill> : <Pill kind="neutral">off</Pill>}
        action={<Button variant={gh ? "secondary" : "primary"} className="px-3 py-1.5 text-[12.5px]"
          onClick={() => ctx.openWizard({ kind: "github-signin", step: 1 })}>
          {gh ? "Reconfigure →" : "Set up GitHub sign-in →"}
        </Button>}>
        Let anyone with a GitHub account sign in. New accounts wait on your Approvals page until you
        let them in. <span className="text-ink3">Takes ~2 minutes — we'll walk you through creating the OAuth app.</span>
      </Card>

      <Card title="Single sign-on (OIDC)" pill={oidc ? <Pill kind="ok">✓ on</Pill> : <Pill kind="neutral">off</Pill>}
        action={<Button variant="secondary" className="px-3 py-1.5 text-[12.5px]"
          onClick={() => ctx.openWizard({ kind: "oidc", step: 1 })}>
          {oidc ? "Reconfigure →" : "Set up SSO →"}
        </Button>}>
        Connect your identity provider (Entra ID, Keycloak, Auth0, …) so users sign in with your org's accounts.
      </Card>

      <Card title="Local admin" pill={<Pill kind="ok">✓ active</Pill>}
        action={<Button variant="secondary" disabled className="px-3 py-1.5 text-[12.5px]"
          title="No self-service password change yet — set Naudit:Ui:Admins via environment.">
          Change password
        </Button>}>
        <span className="font-mono text-ink2">password set via environment</span>
      </Card>

      <p className="mt-1 text-[12px] text-ink3">
        Need a key that isn't here? Flip on <b className="text-ink2">Raw keys</b> in the sidebar to edit the full config catalog.
      </p>
    </div>
  );
}
