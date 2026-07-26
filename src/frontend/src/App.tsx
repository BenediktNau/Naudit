import { useCallback, useEffect, useRef, useState } from "react";
import { AuthGate } from "@/lib/auth";
import { SetupGate } from "@/components/setup/SetupGate";
import { Sidebar } from "@/components/Sidebar";
import { CommandPalette, type PaletteTarget } from "@/components/CommandPalette";
import { DashboardPage } from "@/components/pages/DashboardPage";
import { ApprovalsPage } from "@/components/pages/ApprovalsPage";
import { SettingsPage } from "@/components/pages/SettingsPage";
import { ProfilePage } from "@/components/pages/ProfilePage";
import { MemoryPage } from "@/components/pages/MemoryPage";
import { AnalyticsPage } from "@/components/pages/AnalyticsPage";

export type AppPage = "dashboard" | "approvals" | "settings" | "profile" | "memory" | "analytics";

/** Was das Dashboard nach einem Sprung aus der Palette aufklappen soll.
 *  `nonce` zählt hoch, damit derselbe Treffer zweimal hintereinander wieder greift. */
export type DashboardFocus = { projectId?: number; reviewId?: number; nonce: number };

// Reihenfolge der Navigation — sie bestimmt, ob die neue Seite von unten oder von oben einfliegt.
const ORDER: AppPage[] = ["dashboard", "memory", "analytics", "approvals", "settings", "profile"];

function Shell() {
  const [page, setPage] = useState<AppPage>("dashboard");
  const [down, setDown] = useState(false);
  const [palette, setPalette] = useState(false);
  const [focus, setFocus] = useState<DashboardFocus | null>(null);
  const nonce = useRef(0);

  const navigate = useCallback((next: AppPage) => {
    setPage((current) => {
      setDown(ORDER.indexOf(next) < ORDER.indexOf(current));
      return next;
    });
  }, []);

  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        setPalette(true);
      }
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const runTarget = useCallback(
    (t: PaletteTarget) => {
      setPalette(false);
      navigate(t.page);
      if (t.projectId !== undefined || t.reviewId !== undefined) {
        nonce.current += 1;
        setFocus({ projectId: t.projectId, reviewId: t.reviewId, nonce: nonce.current });
      }
    },
    [navigate],
  );

  return (
    <div className="flex h-full min-h-0 flex-col md:flex-row">
      <Sidebar page={page} onNavigate={navigate} onOpenPalette={() => setPalette(true)} />

      <main className="flex min-h-0 min-w-0 flex-1 flex-col overflow-y-auto">
        {/* key = Seite: der Einflug-Keyframe startet bei jedem Wechsel neu. */}
        <div
          key={page}
          className={`mx-auto w-full max-w-[1280px] px-5 pt-6 pb-14 md:px-6 ${down ? "animate-swipedown" : "animate-swipeup"}`}
        >
          {page === "dashboard" && <DashboardPage focus={focus} />}
          {page === "approvals" && <ApprovalsPage />}
          {page === "settings" && <SettingsPage />}
          {page === "profile" && <ProfilePage />}
          {page === "memory" && <MemoryPage />}
          {page === "analytics" && <AnalyticsPage />}
        </div>
      </main>

      {palette && <CommandPalette onClose={() => setPalette(false)} onRun={runTarget} />}
    </div>
  );
}

export default function App() {
  return (
    <SetupGate>
      <AuthGate>
        <Shell />
      </AuthGate>
    </SetupGate>
  );
}
