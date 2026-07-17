import { useState } from "react";
import { useDashboard, useProjectMemory } from "@/hooks/queries";
import { useCreateConvention, useToggleMemoryEntry } from "@/hooks/mutations";
import { Skeleton } from "@/components/ui/Skeleton";

const kindPill: Record<string, string> = {
  FalsePositive: "text-warn bg-warn/12",
  Convention: "text-teal bg-teal/12",
};

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

  if (isLoading) return <div className="p-7"><Skeleton className="h-4 w-64" /></div>;
  if (!dash || dash.projects.length === 0)
    return <div className="p-7 font-mono text-[13px] text-ink3">No reviewed projects yet — memory entries attach to projects.</div>;

  const submit = () => {
    const t = text.trim();
    if (!t) return;
    create.mutate({ text: t, file: file.trim() || undefined }, { onSuccess: () => { setText(""); setFile(""); } });
  };

  return (
    <div className="flex flex-col gap-5 p-7">
      <div className="flex items-center gap-3">
        <h1 className="text-[15px] font-semibold text-ink">Project memory</h1>
        <select
          className="rounded-lg border border-border bg-elev px-2.5 py-1.5 font-mono text-[12.5px] text-ink"
          value={selected ?? undefined}
          onChange={(e) => setProjectId(Number(e.target.value))}
        >
          {dash.projects.map((p) => (
            <option key={p.id} value={p.id}>{p.name}</option>
          ))}
        </select>
      </div>

      {/* Konvention anlegen */}
      <div className="flex flex-wrap items-center gap-2">
        <input
          className="min-w-[32ch] flex-1 rounded-lg border border-border bg-elev px-2.5 py-1.5 text-[13px] text-ink placeholder:text-ink3"
          placeholder="New convention — e.g. “German code comments are intentional”"
          value={text}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => e.key === "Enter" && submit()}
        />
        <input
          className="w-[24ch] rounded-lg border border-border bg-elev px-2.5 py-1.5 font-mono text-[12px] text-ink placeholder:text-ink3"
          placeholder="file scope (optional)"
          value={file}
          onChange={(e) => setFile(e.target.value)}
          onKeyDown={(e) => e.key === "Enter" && submit()}
        />
        <button
          className="rounded-lg bg-acc/12 px-3 py-1.5 text-[13px] font-semibold text-acc disabled:opacity-50"
          disabled={!text.trim() || create.isPending}
          onClick={submit}
        >
          Add
        </button>
      </div>

      {/* Einträge */}
      {memLoading && <Skeleton className="h-3 w-full max-w-[70ch]" />}
      {memory && memory.entries.length === 0 && (
        <div className="font-mono text-[12.5px] text-ink3">No memory entries yet. Mark a finding as false positive or add a convention.</div>
      )}
      {memory && memory.entries.length > 0 && (
        <div className="flex flex-col gap-2">
          {memory.entries.map((m) => (
            <div key={m.id} className={`flex items-start gap-2.5 ${m.active ? "" : "opacity-50"}`}>
              <span className={`mt-px shrink-0 rounded px-1.5 py-0.5 font-mono text-[10px] ${kindPill[m.kind]}`}>
                {m.kind === "FalsePositive" ? "FP" : "convention"}
              </span>
              <div className="min-w-0 flex-1 text-[12.5px] leading-snug text-ink2">
                {m.file && <span className="font-mono text-ink3">{m.file} — </span>}
                {m.text}
                {m.reason && <span className="text-ink3"> · {m.reason}</span>}
                <span className="ml-1.5 font-mono text-[10.5px] text-ink3">
                  {m.createdBy} · {new Date(m.createdAt).toLocaleDateString()}
                </span>
              </div>
              <button
                className="shrink-0 rounded px-1.5 py-0.5 font-mono text-[10px] text-ink3 hover:text-ink"
                title={m.active ? "Deactivate (kept for audit)" : "Reactivate"}
                onClick={() => toggle.mutate({ id: m.id, active: !m.active })}
              >
                {m.active ? "deactivate" : "activate"}
              </button>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
