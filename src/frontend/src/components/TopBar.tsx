import { useAuth } from "@/lib/auth";
import { Logo } from "@/components/ui/Logo";
import type { AppPage } from "@/App";

const tab = (active: boolean) =>
  `rounded-lg px-3 py-1.5 text-[13px] ${active ? "bg-acc/12 font-semibold text-acc" : "font-medium text-ink2 hover:text-ink"}`;

export function TopBar({ page, onNavigate }: { page: AppPage; onNavigate: (p: AppPage) => void }) {
  const { me, logout } = useAuth();
  return (
    <div className="flex items-center border-b border-hairline px-7 py-3.5">
      <span className="inline-flex items-center gap-2.5">
        <Logo size={24} />
        <span className="font-mono text-base font-bold text-white">naudit</span>
      </span>
      <nav className="mr-auto ml-8 flex gap-1.5">
        <button className={tab(page === "dashboard")} onClick={() => onNavigate("dashboard")}>
          Dashboard
        </button>
        <button className={tab(page === "memory")} onClick={() => onNavigate("memory")}>
          Memory
        </button>
        <button className={tab(page === "analytics")} onClick={() => onNavigate("analytics")}>
          Auswertung
        </button>
        {me.isAdmin && (
          <button className={tab(page === "approvals")} onClick={() => onNavigate("approvals")}>
            Approvals
          </button>
        )}
        {me.isAdmin && (
          <button className={tab(page === "settings")} onClick={() => onNavigate("settings")}>
            Settings
          </button>
        )}
      </nav>
      <span className="flex items-center gap-2.5 font-mono text-[12.5px] text-ink2">
        <button className="flex items-center gap-2.5 hover:text-ink" onClick={() => onNavigate("profile")} title="Profile">
          <span className="grid size-6 place-items-center rounded-full border border-border bg-elev text-[11px] font-bold text-acc">
            {me.username?.slice(0, 1).toUpperCase()}
          </span>
          {me.username}
        </button>
        {me.isAdmin && (
          <span className="rounded-full border border-current px-2 py-0.5 text-[10px] tracking-widest text-teal uppercase">
            admin
          </span>
        )}
        <button className="text-ink3 hover:text-ink" onClick={() => void logout()} title="Sign out">
          ⏻
        </button>
      </span>
    </div>
  );
}
