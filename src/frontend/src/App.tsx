import { useState } from "react";
import { AuthGate } from "@/lib/auth";
import { TopBar } from "@/components/TopBar";
import { DashboardPage } from "@/components/pages/DashboardPage";
import { ApprovalsPage } from "@/components/pages/ApprovalsPage";
import { SettingsPage } from "@/components/pages/SettingsPage";
import { ProfilePage } from "@/components/pages/ProfilePage";

export type AppPage = "dashboard" | "approvals" | "settings" | "profile";

function Shell() {
  const [page, setPage] = useState<AppPage>("dashboard");
  return (
    <div className="mx-auto flex min-h-full max-w-[1200px] flex-col">
      <TopBar page={page} onNavigate={setPage} />
      {page === "dashboard" && <DashboardPage />}
      {page === "approvals" && <ApprovalsPage />}
      {page === "settings" && <SettingsPage />}
      {page === "profile" && <ProfilePage />}
    </div>
  );
}

export default function App() {
  return (
    <AuthGate>
      <Shell />
    </AuthGate>
  );
}
