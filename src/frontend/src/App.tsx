import { useState } from "react";
import { AuthGate } from "@/lib/auth";
import { TopBar } from "@/components/TopBar";

export type AppPage = "dashboard" | "approvals" | "settings" | "profile";

function Placeholder({ name }: { name: string }) {
  return <div className="p-8 font-mono text-ink3">{name} — folgt</div>;
}

function Shell() {
  const [page, setPage] = useState<AppPage>("dashboard");
  return (
    <div className="mx-auto flex min-h-full max-w-[1200px] flex-col">
      <TopBar page={page} onNavigate={setPage} />
      {page === "dashboard" && <Placeholder name="dashboard" />}
      {page === "approvals" && <Placeholder name="approvals" />}
      {page === "settings" && <Placeholder name="settings" />}
      {page === "profile" && <Placeholder name="profile" />}
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
