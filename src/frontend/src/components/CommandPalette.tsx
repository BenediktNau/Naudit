import { useEffect, useMemo, useRef, useState } from "react";
import type { AppPage } from "@/App";
import { useAuth } from "@/lib/auth";
import { useDashboard, fmtTokens } from "@/hooks/queries";
import { Icon, ICON, SearchIcon } from "@/components/ui/icons";

export type PaletteTarget = { page: AppPage; projectId?: number; reviewId?: number };

type Entry = PaletteTarget & { label: string; sub: string; icon: string };

/** Sprungmarken-Suche (⌘K): Seiten, Projekte und zuletzt geprüfte PRs in einer Liste.
 *  Rein clientseitig über die Daten, die das Dashboard ohnehin schon geladen hat. */
export function CommandPalette({ onClose, onRun }: { onClose: () => void; onRun: (t: PaletteTarget) => void }) {
  const { me } = useAuth();
  const { data } = useDashboard();
  const [query, setQuery] = useState("");
  const [cursor, setCursor] = useState(0);
  const listRef = useRef<HTMLDivElement>(null);

  const all = useMemo<Entry[]>(() => {
    const pages: Entry[] = [
      { page: "dashboard", label: "Overview", sub: "Dashboard", icon: ICON.dashboard },
      { page: "memory", label: "Project memory", sub: "Memory", icon: ICON.memory },
      { page: "analytics", label: "Auswertung", sub: "Analytics", icon: ICON.analytics },
      { page: "profile", label: "Your profile", sub: "Account · sessions · usage", icon: ICON.profile },
      ...(me.isAdmin
        ? ([
            { page: "approvals", label: "Access", sub: "Accounts & approvals", icon: ICON.approvals },
            { page: "settings", label: "Settings", sub: "Instance configuration", icon: ICON.settings },
          ] as Entry[])
        : []),
    ];
    const projects: Entry[] = (data?.projects ?? []).map((p) => ({
      page: "dashboard",
      projectId: p.id,
      label: p.name,
      sub: `Repository · ${fmtTokens(p.totalTokens)} tokens`,
      icon: ICON.repo,
    }));
    const reviews: Entry[] = (data?.recentReviews ?? []).map((r) => ({
      page: "dashboard",
      reviewId: r.id,
      label: r.title,
      sub: `${r.project} #${r.prNumber}`,
      icon: ICON.pr,
    }));
    return [...pages, ...projects, ...reviews];
  }, [data, me.isAdmin]);

  const results = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return all.slice(0, 8);
    return all.filter((e) => `${e.label} ${e.sub}`.toLowerCase().includes(q)).slice(0, 8);
  }, [all, query]);

  // Der Cursor darf nicht hinter das (mit jedem Tastendruck kürzere) Ergebnis rutschen.
  const active = results.length === 0 ? 0 : Math.min(cursor, results.length - 1);

  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") {
        e.preventDefault();
        onClose();
        return;
      }
      if (e.key === "ArrowDown" || e.key === "ArrowUp") {
        e.preventDefault();
        if (results.length === 0) return;
        const step = e.key === "ArrowDown" ? 1 : -1;
        setCursor((c) => (Math.min(c, results.length - 1) + step + results.length) % results.length);
        return;
      }
      if (e.key === "Enter") {
        e.preventDefault();
        const hit = results[active];
        if (hit) onRun(hit);
      }
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [results, active, onClose, onRun]);

  // Bei Tastatur-Navigation den aktiven Treffer in den Scrollbereich holen.
  useEffect(() => {
    listRef.current?.children[active]?.scrollIntoView({ block: "nearest" });
  }, [active]);

  return (
    <div className="fixed inset-0 z-60 flex items-start justify-center px-5 pt-[14vh] pb-5">
      <button
        onClick={onClose}
        aria-label="Close search"
        className="absolute inset-0 animate-veil cursor-default bg-[rgba(5,8,11,.66)] backdrop-blur-[3px]"
        style={{ animationDuration: ".2s" }}
      />
      <div
        role="dialog"
        aria-modal="true"
        aria-label="Jump to"
        className="relative w-[560px] max-w-full animate-popin overflow-hidden rounded-[15px] border border-[#2b3542]
                   bg-[#12181f] shadow-[0_40px_80px_-24px_rgba(0,0,0,.8)]"
      >
        <div className="flex items-center gap-2.5 border-b border-hairline px-4 py-3.5">
          <span className="text-ink3">
            <SearchIcon size={15} />
          </span>
          <input
            autoFocus
            value={query}
            onChange={(e) => {
              setQuery(e.target.value);
              setCursor(0);
            }}
            placeholder="Jump to a page, project or pull request…"
            aria-label="Search"
            className="flex-1 border-none bg-transparent text-sm text-ink outline-none placeholder:text-ink3"
          />
          <span className="shrink-0 rounded-[5px] bg-hairline px-1.5 py-0.5 font-mono text-[10px] text-ink3">esc</span>
        </div>

        <div ref={listRef} className="max-h-[340px] overflow-y-auto p-1.5">
          {results.map((r, i) => {
            const on = i === active;
            return (
              <button
                key={`${r.page}-${r.projectId ?? ""}-${r.reviewId ?? ""}-${r.label}`}
                onClick={() => onRun(r)}
                onMouseEnter={() => setCursor(i)}
                className={`flex w-full items-center gap-2.5 rounded-[10px] px-2.5 py-2 text-left transition-colors duration-150
                            ${on ? "bg-hairline" : ""}`}
              >
                <span
                  className={`grid size-7 shrink-0 place-items-center rounded-lg transition-colors duration-150
                              ${on ? "bg-acc/14 text-acc" : "bg-seam text-ink3"}`}
                >
                  <Icon path={r.icon} size={13} />
                </span>
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-[13px] text-ink">{r.label}</span>
                  <span className="mt-0.5 block truncate font-mono text-[10.5px] text-ink3">{r.sub}</span>
                </span>
                <span className={`text-xs text-ink3 transition-opacity duration-150 ${on ? "opacity-100" : "opacity-0"}`}>
                  ↵
                </span>
              </button>
            );
          })}
          {results.length === 0 && <div className="px-3.5 py-6 text-center text-[12.5px] text-ink3">No matches.</div>}
        </div>
      </div>
    </div>
  );
}
