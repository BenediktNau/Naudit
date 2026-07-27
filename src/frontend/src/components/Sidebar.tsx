import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import type { AppPage } from "@/App";
import { useAuth } from "@/lib/auth";
import { useAccounts } from "@/hooks/queries";
import { Logo } from "@/components/ui/Logo";
import { Icon, ICON, SearchIcon } from "@/components/ui/icons";

type NavItem = { id: AppPage; label: string; icon: string; adminOnly?: boolean };

const NAV: NavItem[] = [
  { id: "dashboard", label: "Overview", icon: ICON.dashboard },
  { id: "memory", label: "Memory", icon: ICON.memory },
  { id: "analytics", label: "Auswertung", icon: ICON.analytics },
  { id: "approvals", label: "Access", icon: ICON.approvals, adminOnly: true },
  { id: "settings", label: "Settings", icon: ICON.settings, adminOnly: true },
];

type Box = { left: number; top: number; width: number; height: number };

/** Seitliche Navigation. Der aktive Eintrag wird nicht eingefärbt, sondern von einem
 *  gleitenden Hintergrund-Pill unterlegt: eine Bewegung statt fünf Farbwechseln. */
export function Sidebar({
  page,
  onNavigate,
  onOpenPalette,
}: {
  page: AppPage;
  onNavigate: (p: AppPage) => void;
  onOpenPalette: () => void;
}) {
  const { me, logout } = useAuth();
  const { data: accounts } = useAccounts(me.isAdmin);
  const pending = accounts?.pending.length ?? 0;

  const listRef = useRef<HTMLDivElement>(null);
  const itemRefs = useRef(new Map<AppPage, HTMLButtonElement>());
  const [box, setBox] = useState<Box | null>(null);

  const measure = useCallback(() => {
    const list = listRef.current;
    const el = itemRefs.current.get(page);
    if (!list || !el) return;
    const lb = list.getBoundingClientRect();
    const r = el.getBoundingClientRect();
    if (!r.width) return;
    setBox({ left: r.left - lb.left, top: r.top - lb.top, width: r.width, height: r.height });
  }, [page]);

  // Vor dem Paint messen, damit das Pill nicht erst an der falschen Stelle aufblitzt.
  useLayoutEffect(measure, [measure]);

  useEffect(() => {
    const list = listRef.current;
    if (!list) return;
    // Der Umbruch Sidebar ↔ Topbar verschiebt die Einträge, ohne dass sich `page` ändert.
    const ro = new ResizeObserver(measure);
    ro.observe(list);
    return () => ro.disconnect();
  }, [measure]);

  const items = NAV.filter((n) => !n.adminOnly || me.isAdmin);

  return (
    <nav
      className="flex shrink-0 flex-row flex-wrap items-center gap-x-5 gap-y-2 border-b border-hairline bg-nav px-5 py-2.5
                 md:w-[226px] md:flex-col md:flex-nowrap md:items-stretch md:gap-1 md:border-r md:border-b-0 md:px-3 md:py-4"
    >
      <div className="flex shrink-0 items-center gap-2.5 md:px-2 md:pt-1 md:pb-3.5">
        <Logo size={26} />
        <span className="text-[15px] font-semibold tracking-[-.01em] text-white">Naudit</span>
      </div>

      <div ref={listRef} className="relative flex flex-row flex-wrap gap-1 md:flex-1 md:flex-col md:flex-nowrap md:gap-0.5">
        {box && (
          <div
            className="pointer-events-none absolute rounded-[9px] bg-acc/12 transition-[left,top,width,height] duration-300 ease-swift"
            style={box}
            aria-hidden
          />
        )}
        {items.map((n) => {
          const active = page === n.id;
          return (
            <button
              key={n.id}
              ref={(el) => {
                if (el) itemRefs.current.set(n.id, el);
                else itemRefs.current.delete(n.id);
              }}
              onClick={() => onNavigate(n.id)}
              aria-current={active ? "page" : undefined}
              className={`relative flex items-center gap-2.5 rounded-[9px] px-3 py-2 text-left text-[13px] transition-colors duration-200
                          md:w-full ${active ? "font-semibold text-acc" : "font-medium text-ink2 hover:text-ink"}`}
            >
              <Icon path={n.icon} className="opacity-90" />
              <span className="hidden sm:inline">{n.label}</span>
              {n.id === "approvals" && pending > 0 && !active && (
                <span className="rounded-full bg-warn/12 px-1.5 font-mono text-[10px] text-warn">{pending}</span>
              )}
            </button>
          );
        })}
      </div>

      <div className="ml-auto flex shrink-0 items-center gap-2.5 md:mt-auto md:ml-0 md:flex-col md:items-stretch md:gap-1 md:pt-3">
        <button
          onClick={onOpenPalette}
          // Die Beschriftung wird unterhalb von lg ausgeblendet — ohne aria-label bliebe
          // dem Screenreader dort nur "⌘K".
          aria-label="Search"
          className="flex items-center gap-2 rounded-[9px] border border-hairline bg-input px-2.5 py-1.5 text-[12.5px] text-ink3
                     transition-colors duration-200 hover:border-border hover:text-ink2 md:w-full md:py-2"
        >
          <SearchIcon />
          <span className="hidden flex-1 text-left lg:inline">Search</span>
          <span className="font-mono text-[10px] text-ink4">⌘K</span>
        </button>

        <div className="hidden h-px bg-hairline md:block" />

        <div className="flex items-center gap-1">
          <button
            onClick={() => onNavigate("profile")}
            title="Profile"
            className={`flex min-w-0 flex-1 items-center gap-2.5 rounded-[9px] px-2 py-1.5 transition-colors duration-200 hover:bg-elev
                        ${page === "profile" ? "bg-elev" : ""}`}
          >
            <span className="grid size-6.5 shrink-0 place-items-center rounded-full bg-acc/14 text-[11px] font-bold text-acc">
              {me.username?.slice(0, 1).toUpperCase()}
            </span>
            <span className="hidden min-w-0 flex-1 text-left md:block">
              <span className="block truncate text-[12.5px] leading-tight font-medium text-ink">{me.username}</span>
              <span className="block text-[10.5px] leading-snug text-ink3">
                {me.isAdmin ? "Administrator" : "Reviewer"}
              </span>
            </span>
          </button>
          <button
            onClick={() => void logout()}
            title="Sign out"
            aria-label="Sign out"
            className="grid size-7 shrink-0 place-items-center rounded-lg text-ink4 transition-colors duration-200 hover:bg-elev hover:text-ink"
          >
            <Icon path={ICON.signout} size={13} />
          </button>
        </div>
      </div>
    </nav>
  );
}
