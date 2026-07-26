import { useState } from "react";
import { useDashboard, useProjectGuidelines, useProjectMemory } from "@/hooks/queries";
import { useCreateConvention, useRedistillGuidelines, useSaveGuidelines, useToggleMemoryEntry } from "@/hooks/mutations";
import { Panel } from "@/components/ui/Panel";
import { SevTag } from "@/components/ui/Pill";
import { Input, Select, Textarea } from "@/components/ui/Input";
import { PageHeader } from "@/components/ui/PageHeader";
import { EmptyState } from "@/components/ui/EmptyState";
import { Skeleton } from "@/components/ui/Skeleton";

const kindPill: Record<string, string> = {
  FalsePositive: "text-warn bg-warn/12",
  Convention: "text-teal bg-teal/12",
};

const quietBtn =
  "shrink-0 rounded-lg border border-border px-2.5 py-1 text-[11.5px] font-medium text-ink2 transition-colors duration-200 " +
  "hover:border-ink3 hover:text-ink disabled:opacity-50";

const accentBtn =
  "shrink-0 rounded-[10px] bg-acc px-4 py-2 text-[12.5px] font-semibold text-accink transition-[background,transform] " +
  "duration-200 hover:bg-acc2 active:scale-[.97] disabled:opacity-50 disabled:active:scale-100";

/** Projekt-Gedächtnis: FP-Markierungen + Konventionen je Projekt einsehen und pflegen. */
export function MemoryPage() {
  const { data: dash, isLoading } = useDashboard();
  const [projectId, setProjectId] = useState<number | null>(null);
  const selected = projectId ?? dash?.projects[0]?.id ?? null;
  const { data: memory, isLoading: memLoading } = useProjectMemory(selected);
  const create = useCreateConvention(selected);
  const toggle = useToggleMemoryEntry(selected);
  const [text, setText] = useState("");
  const [file, setFile] = useState("");

  if (isLoading) return <Skeleton className="h-4 w-64" />;
  if (!dash || dash.projects.length === 0)
    return (
      <div className="font-mono text-[13px] text-ink3">
        No reviewed projects yet — memory entries attach to projects.
      </div>
    );

  const submit = () => {
    // Enter umgeht den disabled-Button — hier erneut gegen Doppel-Submit sichern.
    if (create.isPending) return;
    const t = text.trim();
    if (!t) return;
    create.mutate(
      { text: t, file: file.trim() || undefined },
      {
        onSuccess: () => {
          setText("");
          setFile("");
        },
      },
    );
  };

  const active = memory?.entries.filter((m) => m.active).length ?? 0;
  const retired = (memory?.entries.length ?? 0) - active;

  return (
    <>
      <PageHeader
        title="Project memory"
        subtitle="What Naudit has learned about this codebase — and stopped flagging."
      >
        <Select aria-label="Project" value={selected ?? undefined} onChange={(e) => setProjectId(Number(e.target.value))}>
          {dash.projects.map((p) => (
            <option key={p.id} value={p.id}>
              {p.name}
            </option>
          ))}
        </Select>
      </PageHeader>

      <div className="flex flex-col gap-3.5">
        {/* Architektur-Profil (destillierte Guidelines) */}
        {/* key erzwingt Remount je Projekt: editing/draft dürfen einen Projektwechsel nicht
            überleben — sonst überschreibt "Save" das NEUE Projekt mit dem alten Entwurf. */}
        <GuidelinesCard key={selected ?? "none"} projectId={selected} />

        {/* Konvention anlegen */}
        <div className="flex flex-wrap gap-2">
          <Input
            className="min-w-0 flex-[1_1_340px]"
            placeholder="New convention — e.g. “German code comments are intentional”"
            aria-label="New convention"
            value={text}
            onChange={(e) => setText(e.target.value)}
            onKeyDown={(e) => e.key === "Enter" && submit()}
          />
          <Input
            className="flex-[0_1_200px] font-mono text-[11.5px]"
            placeholder="File scope (optional)"
            aria-label="File scope"
            value={file}
            onChange={(e) => setFile(e.target.value)}
            onKeyDown={(e) => e.key === "Enter" && submit()}
          />
          <button className={accentBtn} disabled={!text.trim() || create.isPending} onClick={submit}>
            Add
          </button>
        </div>

        {memLoading && <Skeleton className="h-3 w-full max-w-[70ch]" />}

        {memory && (
          <Panel title="Memory entries" extra={`${active} active · ${retired} retired`}>
            {memory.entries.length === 0 && (
              <EmptyState>
                No memory entries yet. Mark a finding as false positive or add a convention above.
              </EmptyState>
            )}
            {memory.entries.map((m) => (
              <div
                key={m.id}
                className={`flex flex-wrap items-start gap-2.5 border-b border-seam px-4 py-3 transition-[opacity,background] duration-200
                            last:border-b-0 hover:bg-elev ${m.active ? "" : "opacity-45"}`}
              >
                <SevTag className={kindPill[m.kind] ?? kindPill.Convention}>
                  {m.kind === "FalsePositive" ? "false positive" : "convention"}
                </SevTag>
                <div className="min-w-[20ch] flex-1 text-[12.5px] leading-snug text-ink2">
                  {m.file && <span className="font-mono text-[11px] text-ink3">{m.file} — </span>}
                  {m.text}
                  {m.reason && <span className="text-ink3"> · {m.reason}</span>}
                  <span className="ml-2 text-[10.5px] text-ink4">
                    {m.createdBy} · {new Date(m.createdAt).toLocaleDateString()}
                  </span>
                </div>
                <button
                  className={quietBtn}
                  title={m.active ? "Deactivate (kept for audit)" : "Reactivate"}
                  disabled={toggle.isPending}
                  onClick={() => toggle.mutate({ id: m.id, active: !m.active })}
                >
                  {m.active ? "Retire" : "Restore"}
                </button>
              </div>
            ))}
          </Panel>
        )}
      </div>
    </>
  );
}

/** Architektur-Profil-Karte: destilliertes Profil anzeigen, kuratieren, Neu-Destillation anstoßen. */
function GuidelinesCard({ projectId }: { projectId: number | null }) {
  const { data, isLoading } = useProjectGuidelines(projectId);
  const save = useSaveGuidelines(projectId);
  const redistill = useRedistillGuidelines(projectId);
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState("");

  if (isLoading || !data) return null;

  const startEdit = () => {
    setDraft(data.markdown ?? "");
    setEditing(true);
  };
  const submit = () => {
    const t = draft.trim();
    if (!t || save.isPending) return;
    save.mutate({ markdown: t }, { onSuccess: () => setEditing(false) });
  };

  const meta = data.markdown
    ? data.pending
      ? "re-distills on the next review — showing the previous profile"
      : `${data.manuallyEdited ? "Curated" : "Distilled"} · ${data.updatedBy ?? ""}${
          data.distilledAt ? ` · ${new Date(data.distilledAt).toLocaleDateString()}` : ""
        }`
    : "distills from the repo's docs on the next review";

  return (
    <Panel
      title="Architecture profile"
      extra={meta}
      actions={
        !editing && (
          <>
            {data.markdown && (
              <button className={quietBtn} onClick={startEdit}>
                Edit
              </button>
            )}
            <button
              className={quietBtn}
              disabled={redistill.isPending}
              title="Discards manual edits; the profile is re-distilled on the next review."
              onClick={() => {
                if (window.confirm("Re-distill from repository docs on the next review? Manual edits are discarded."))
                  redistill.mutate();
              }}
            >
              Re-distill
            </button>
          </>
        )
      }
    >
      {data.sourcesChangedAt && (
        <div className="border-b border-seam px-4 py-2.5 font-mono text-[11px] text-warn">
          Repository docs changed since this profile was curated — “Re-distill” to rebuild it.
        </div>
      )}
      {!editing &&
        (data.markdown ? (
          <pre className="m-0 p-4 font-mono text-[11.5px] leading-[1.75] whitespace-pre-wrap text-ink2">
            {data.markdown}
          </pre>
        ) : (
          <EmptyState>No profile yet.</EmptyState>
        ))}
      {editing && (
        <div className="flex flex-col gap-2.5 p-4">
          <Textarea
            className="min-h-40 w-full text-[11.5px]"
            aria-label="Architecture profile"
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
          />
          <div className="flex gap-2">
            <button className={accentBtn} disabled={!draft.trim() || save.isPending} onClick={submit}>
              Save
            </button>
            <button className={quietBtn} onClick={() => setEditing(false)}>
              Cancel
            </button>
          </div>
        </div>
      )}
    </Panel>
  );
}
