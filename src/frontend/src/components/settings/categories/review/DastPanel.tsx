import { Panel } from "@/components/ui/Panel";
import { Field } from "@/components/setup/shared";
import { Toggle } from "../../primitives";
import type { SettingsCtx } from "../../model";
import { selCls } from "./shared";

const KEY_ENABLED = "Naudit:Review:Dast:Enabled";
const KEY_PROJECTS = "Naudit:Review:Dast:Projects";

export function DastPanel({ ctx }: { ctx: SettingsCtx }) {
  const on = ctx.get(KEY_ENABLED) === "true";
  const projects = ctx.get(KEY_PROJECTS).split(",").map((s) => s.trim()).filter(Boolean);

  return (
    <Panel title="Dynamic testing (DAST)" extra={on ? `${projects.length} project(s)` : "off"}>
      <div className="flex flex-col gap-4 px-5 py-4">
        <div className="flex items-center justify-between gap-4">
          <div>
            <div className="text-[13px] font-medium text-ink">Build and probe the PR's app</div>
            <p className="mt-0.5 text-[12.5px] text-ink2">
              Runs the pull request's own Dockerfile in an isolated container and probes it through
              a browser. Requires the host Docker socket to be mounted.
            </p>
          </div>
          <Toggle on={on} disabled={ctx.locked(KEY_ENABLED)} aria-label="Enable DAST"
            onChange={(v) => ctx.set(KEY_ENABLED, String(v))} />
        </div>

        <Field label="Allowed projects"
          hint="One per line — owner/repo (GitHub) or the GitLab project id. Empty means no project runs.">
          <textarea rows={3} disabled={ctx.locked(KEY_PROJECTS)}
            className="min-h-[72px] w-full rounded-lg border border-border bg-bg px-3 py-2 font-mono text-[13px] text-ink outline-none placeholder:text-ink3 focus:border-acc disabled:opacity-50"
            placeholder="acme/web"
            value={projects.join("\n")}
            onChange={(e) => ctx.set(KEY_PROJECTS,
              e.target.value.split("\n").map((s) => s.trim()).filter(Boolean).join(","))} />
        </Field>

        {on && projects.length === 0 && (
          <div className="rounded-lg border border-warn/40 bg-warn/10 px-4 py-3 text-[12.5px] text-ink2">
            DAST is on but no project is allowlisted — nothing will run. This is deliberate: dynamic
            testing executes untrusted pull-request code, so it is opt-in per project.
          </div>
        )}

        <div className="flex flex-wrap gap-4">
          <Field label="Dockerfile path" hint="Relative to the repo root.">
            <input className={selCls} placeholder="Dockerfile (default)"
              disabled={ctx.locked("Naudit:Review:Dast:DockerfilePath")}
              value={ctx.get("Naudit:Review:Dast:DockerfilePath")}
              onChange={(e) => ctx.set("Naudit:Review:Dast:DockerfilePath", e.target.value)} />
          </Field>
          <Field label="App port" hint="Port the app listens on.">
            <input type="number" className={selCls} placeholder="8080 (default)"
              disabled={ctx.locked("Naudit:Review:Dast:AppPort")}
              value={ctx.get("Naudit:Review:Dast:AppPort")}
              onChange={(e) => ctx.set("Naudit:Review:Dast:AppPort", e.target.value)} />
          </Field>
          <Field label="Health path" hint="Polled until the app answers.">
            <input className={selCls} placeholder="/ (default)"
              disabled={ctx.locked("Naudit:Review:Dast:HealthPath")}
              value={ctx.get("Naudit:Review:Dast:HealthPath")}
              onChange={(e) => ctx.set("Naudit:Review:Dast:HealthPath", e.target.value)} />
          </Field>
        </div>
      </div>
    </Panel>
  );
}
