import { useState } from "react";
import { Modal } from "../primitives";
import { Button } from "@/components/ui/Button";
import { Field, CopyRow } from "@/components/setup/shared";
import { useRestartApp, useSaveSettings } from "@/hooks/queries";
import type { SettingsCtx, WizardState } from "../model";

const inputCls =
  "w-full rounded-lg border border-border bg-bg px-3 py-2 font-mono text-[13px] text-ink outline-none placeholder:text-ink3 focus:border-acc";

type Change = { key: string; value: string | null };

export function SignInWizard({ state, ctx, base, onClose }: {
  state: NonNullable<WizardState>; ctx: SettingsCtx; base: string; onClose: () => void;
}) {
  const save = useSaveSettings();
  const restart = useRestartApp();
  const [step, setStep] = useState(state.step);
  const [clientId, setClientId] = useState(ctx.get(
    state.kind === "oidc" ? "Naudit:Ui:Auth:Oidc:ClientId" : "Naudit:Ui:Auth:GitHub:ClientId"));
  const [secret, setSecret] = useState("");
  const [appId, setAppId] = useState(ctx.get("Naudit:GitHub:App:AppId"));
  const [pem, setPem] = useState("");
  const [authority, setAuthority] = useState(ctx.get("Naudit:Ui:Auth:Oidc:Authority"));

  const commit = (changes: Change[], onDone: () => void) =>
    save.mutate(changes.filter((c) => c.value !== ""), { onSuccess: onDone });

  // --- GitHub App: Ein-Schritt-Formular ---
  if (state.kind === "github-app") {
    return (
      <Modal title="Set up GitHub App" onClose={onClose}
        footer={<>
          <button type="button" className="font-mono text-[12px] text-ink3 hover:text-ink" onClick={onClose}>Cancel</button>
          <Button loading={save.isPending}
            disabled={save.isPending || !appId || (!pem && !ctx.secretSet("Naudit:GitHub:App:PrivateKey"))}
            onClick={() => commit([
              { key: "Naudit:GitHub:Auth", value: "App" },
              { key: "Naudit:GitHub:App:AppId", value: appId },
              { key: "Naudit:GitHub:App:PrivateKey", value: pem === "" ? null : pem },
            ], onClose)}>Save</Button>
        </>}>
        <p className="text-[12.5px] text-ink2">Enter the App's credentials. Reviews will post under the bot identity after a restart.</p>
        <Field label="App ID"><input className={inputCls} value={appId} onChange={(e) => setAppId(e.target.value)} /></Field>
        <Field label="Private key (PEM)" hint="Raw PEM or base64. Stored encrypted.">
          <textarea rows={4} className={`${inputCls} min-h-[88px]`} value={pem}
            placeholder={ctx.secretSet("Naudit:GitHub:App:PrivateKey") ? "•••••• (set — leave blank to keep)" : ""}
            onChange={(e) => setPem(e.target.value)} />
        </Field>
        {save.isError && <div className="text-[12px] text-danger">Couldn't save: {save.error?.message}</div>}
      </Modal>
    );
  }

  // --- OIDC: Ein-Schritt-Formular ---
  if (state.kind === "oidc") {
    return (
      <Modal title="Set up single sign-on (OIDC)" onClose={onClose}
        footer={<>
          <button type="button" className="font-mono text-[12px] text-ink3 hover:text-ink" onClick={onClose}>Cancel</button>
          <Button loading={save.isPending}
            disabled={save.isPending || !authority || !clientId || (!secret && !ctx.secretSet("Naudit:Ui:Auth:Oidc:ClientSecret"))}
            onClick={() => commit([
              { key: "Naudit:Ui:Auth:Oidc:Enabled", value: "true" },
              { key: "Naudit:Ui:Auth:Oidc:Authority", value: authority },
              { key: "Naudit:Ui:Auth:Oidc:ClientId", value: clientId },
              { key: "Naudit:Ui:Auth:Oidc:ClientSecret", value: secret === "" ? null : secret },
            ], onClose)}>Save</Button>
        </>}>
        <p className="text-[12.5px] text-ink2">Point Naudit at your IdP. Sign-in turns on after a restart.</p>
        <Field label="Authority" hint="e.g. https://login.microsoftonline.com/{tenant}/v2.0">
          <input className={inputCls} value={authority} onChange={(e) => setAuthority(e.target.value)} /></Field>
        <Field label="Client ID"><input className={inputCls} value={clientId} onChange={(e) => setClientId(e.target.value)} /></Field>
        <Field label="Client secret" hint="Stored encrypted.">
          <input type="password" className={inputCls} value={secret}
            placeholder={ctx.secretSet("Naudit:Ui:Auth:Oidc:ClientSecret") ? "•••••• (set — leave blank to keep)" : ""}
            onChange={(e) => setSecret(e.target.value)} /></Field>
        {save.isError && <div className="text-[12px] text-danger">Couldn't save: {save.error?.message}</div>}
      </Modal>
    );
  }

  // --- GitHub sign-in: 3 Schritte ---
  const dots = (
    <div className="flex items-center gap-1.5">
      {[1, 2, 3].map((n) => <span key={n} className={`size-1.5 rounded-full ${n === step ? "bg-acc" : "bg-border"}`} />)}
    </div>
  );

  return (
    <Modal title="Set up GitHub sign-in" step={`step ${step} / 3`} onClose={onClose}
      footer={
        step === 1 ? (
          <>
            <button type="button" className="font-mono text-[12px] text-ink3 hover:text-ink" onClick={onClose}>Cancel</button>
            <div className="flex items-center gap-4">{dots}
              <Button disabled={!clientId || (!secret && !ctx.secretSet("Naudit:Ui:Auth:GitHub:ClientSecret"))}
                onClick={() => setStep(2)}>Continue →</Button></div>
          </>
        ) : step === 2 ? (
          <>
            <button type="button" className="font-mono text-[12px] text-ink3 hover:text-ink" onClick={() => setStep(1)}>← Back</button>
            <div className="flex items-center gap-4">{dots}
              <Button loading={save.isPending}
                onClick={() => commit([
                  { key: "Naudit:Ui:Auth:GitHub:Enabled", value: "true" },
                  { key: "Naudit:Ui:Auth:GitHub:ClientId", value: clientId },
                  { key: "Naudit:Ui:Auth:GitHub:ClientSecret", value: secret === "" ? null : secret },
                ], () => setStep(3))}>Continue →</Button></div>
          </>
        ) : (
          <>
            <button type="button" className="font-mono text-[12px] text-ink3 hover:text-ink" onClick={onClose}>Restart later</button>
            <div className="flex items-center gap-4">{dots}
              <Button loading={restart.isPending} onClick={() => restart.mutate(undefined, { onSuccess: onClose })}>
                Restart now to apply</Button></div>
          </>
        )
      }>
      {step === 1 && (
        <>
          <p className="text-[12.5px] text-ink2">Create an OAuth app on GitHub and paste its credentials here.</p>
          <ol className="flex flex-col gap-3">
            <li className="text-[12.5px] text-ink2">
              1. Open <a className="text-acc hover:underline" target="_blank" rel="noreferrer"
                href="https://github.com/settings/applications/new">GitHub → Settings → Developer settings → New OAuth app</a>.
            </li>
            <li className="text-[12.5px] text-ink2">2. Set the callback URL:
              <div className="mt-1.5"><CopyRow label="Authorization callback URL" value={`${base}/auth/callback/github`} /></div>
            </li>
            <li className="text-[12.5px] text-ink2">3. Paste the Client ID and secret:</li>
          </ol>
          <Field label="Client ID"><input className={inputCls} value={clientId} onChange={(e) => setClientId(e.target.value)} /></Field>
          <Field label="Client secret"><input type="password" className={inputCls} value={secret} onChange={(e) => setSecret(e.target.value)} /></Field>
        </>
      )}
      {step === 2 && (
        <>
          <p className="text-[12.5px] text-ink2">
            Review your credentials, then Continue to save. GitHub sign-in turns on after a restart — we can't test it before then.
          </p>
          <div className="flex flex-col gap-2 rounded-[10px] border border-hairline bg-bg px-4 py-3 text-[12.5px] text-ink2">
            <span>Client ID will be saved</span>
            <span>Callback URL will be set</span>
          </div>
          {save.isError && <div className="text-[12px] text-danger">Couldn't save: {save.error?.message}</div>}
        </>
      )}
      {step === 3 && (
        <div className="flex flex-col items-center gap-3 text-center">
          <div className="grid size-[52px] place-items-center rounded-full border border-acc bg-acc/12 text-[22px] text-acc">✓</div>
          <b className="text-[15px]">GitHub sign-in is ready</b>
          <p className="max-w-[46ch] text-[12.5px] text-ink2">
            After the restart, a "Continue with GitHub" button appears on the login page. New sign-ins wait on your Approvals page.
          </p>
          {restart.isError && (
            <div className="text-[12px] text-danger">Restart failed: {restart.error?.message ?? "unknown error"}</div>
          )}
        </div>
      )}
    </Modal>
  );
}
